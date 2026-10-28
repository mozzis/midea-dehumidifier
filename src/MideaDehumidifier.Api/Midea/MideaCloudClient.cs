using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace MideaDehumidifier.Api.Midea;

public sealed class MideaCloudException(string message, int? code = null) : Exception(message)
{
    public int? Code { get; } = code;
}

/// <summary>
/// Minimal client for the Midea cloud, used only to obtain the V3 token and key that the device needs
/// for local access. Request shapes follow the open-source msmart-ng project. Supports the NetHome Plus
/// and Midea SmartHome (MSmartHome) app backends; use whichever app the dehumidifier is registered in.
/// </summary>
public sealed class MideaCloudClient(HttpClient http, IOptions<MideaOptions> options, TimeProvider time)
{
    private const string Language = "en_US";
    private static readonly string AppDeviceId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));

    // NetHome Plus
    private const string NetHomeAppId = "1017";
    private const string NetHomeBaseUrl = "https://mapp.appsmb.com";
    private const string NetHomeAppKey = "3742e9e5842d4ad59c2db887e12449f9";

    // Midea SmartHome
    private const string SmartHomeAppId = "1010";
    private const string SmartHomeBaseUrl = "https://mp-prod.appsmb.com";
    private const string SmartHomeHmacKey = "PROD_VnoClJI9aikS8dyy";
    private const string SmartHomeIotKey = "meicloud";
    private const string SmartHomeLoginKey = "ac21b9f9cbfe4ca5a88562ef25e2b768";

    private bool IsSmartHome => string.Equals(options.Value.Cloud, "SmartHome", StringComparison.OrdinalIgnoreCase);

    public async Task<(string Token, string Key)> GetTokenAsync(string udpid, long applianceId, CancellationToken ct)
    {
        var o = options.Value;
        if (string.IsNullOrWhiteSpace(o.CloudAccount) || string.IsNullOrWhiteSpace(o.CloudPassword))
            throw new MideaCloudException(
                "The device needs a V3 token/key. Set Midea:CloudAccount and Midea:CloudPassword (your Midea app login), " +
                "or set Midea:Token and Midea:Key directly.");

        // One login is reused for every request, so the Midea app's "logged in devices" list gets one entry,
        // not one per request. A failure drops the session so the next attempt logs in afresh.
        try
        {
            return IsSmartHome
                ? await GetTokenSmartHomeAsync(udpid, applianceId, ct)
                : await GetTokenNetHomeAsync(udpid, applianceId, ct);
        }
        catch (Exception e) when (e is MideaCloudException or HttpRequestException)
        {
            _smartHomeAccessToken = "";
            _netHomeSessionId = "";
            throw;
        }
    }

    private string Stamp() => time.GetUtcNow().ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);

    private static string Sha256Hex(string s) => Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(s)));
    private static string Md5Hex(string s) => Convert.ToHexStringLower(MD5.HashData(Encoding.ASCII.GetBytes(s)));

    private static (string Token, string Key) FindToken(JsonNode? data, string udpid)
    {
        foreach (var t in data?["tokenlist"]?.AsArray() ?? [])
        {
            if (string.Equals((string?)t?["udpId"], udpid, StringComparison.OrdinalIgnoreCase))
                return ((string)t!["token"]!, (string)t["key"]!);
        }
        throw new MideaCloudException($"The cloud has no token/key for udpid {udpid}.");
    }

    #region NetHome Plus

    private string _netHomeSessionId = "";

    private async Task<(string Token, string Key)> GetTokenNetHomeAsync(string udpid, long applianceId, CancellationToken ct)
    {
        if (_netHomeSessionId.Length == 0) _netHomeSessionId = await NetHomeLoginAsync(ct);

        var result = await NetHomeRequestAsync("/v1/iot/secure/getToken", _netHomeSessionId,
            new() { ["udpid"] = udpid, ["applianceCodes"] = applianceId.ToString(CultureInfo.InvariantCulture) }, ct);
        return FindToken(result, udpid);
    }

    private async Task<string> NetHomeLoginAsync(CancellationToken ct)
    {
        var o = options.Value;
        var loginId = (string?)(await NetHomeRequestAsync("/v1/user/login/id/get", "", new() { ["loginAccount"] = o.CloudAccount }, ct))?["loginId"]
            ?? throw new MideaCloudException("Cloud did not return a login ID.");

        var passwordHash = Sha256Hex(loginId + Sha256Hex(o.CloudPassword) + NetHomeAppKey);
        var login = await NetHomeRequestAsync("/v1/user/login", "",
            new() { ["loginAccount"] = o.CloudAccount, ["password"] = passwordHash }, ct);
        return (string?)login?["sessionId"] ?? throw new MideaCloudException("Cloud login did not return a session.");
    }

    private async Task<JsonNode?> NetHomeRequestAsync(string endpoint, string sessionId, Dictionary<string, string> data, CancellationToken ct)
    {
        var body = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["appId"] = NetHomeAppId,
            ["src"] = NetHomeAppId,
            ["format"] = "2",
            ["clientType"] = "1",
            ["language"] = Language,
            ["deviceId"] = AppDeviceId,
            ["stamp"] = Stamp(),
            ["sessionId"] = sessionId,
        };
        foreach (var (k, v) in data) body[k] = v;

        var query = string.Join("&", body.Select(kv => $"{kv.Key}={kv.Value}"));
        body["sign"] = Sha256Hex(endpoint + query + NetHomeAppKey);

        using var response = await http.PostAsync(NetHomeBaseUrl + endpoint, new FormUrlEncodedContent(body), ct);
        response.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));

        var code = int.Parse((string?)json?["errorCode"] ?? "-1", CultureInfo.InvariantCulture);
        if (code != 0) throw new MideaCloudException($"Midea cloud error {code}: {(string?)json?["msg"]}", code);
        return json?["result"];
    }

    #endregion

    #region SmartHome

    private string _smartHomeAccessToken = "";

    private async Task<(string Token, string Key)> GetTokenSmartHomeAsync(string udpid, long applianceId, CancellationToken ct)
    {
        if (_smartHomeAccessToken.Length == 0) await SmartHomeLoginAsync(ct);

        var result = await SmartHomeRequestAsync("/v1/iot/secure/getToken",
            SmartHomeBody(new()
            {
                ["udpid"] = udpid,
                ["applianceCodes"] = applianceId.ToString(CultureInfo.InvariantCulture),
            }), ct);
        return FindToken(result, udpid);
    }

    private async Task SmartHomeLoginAsync(CancellationToken ct)
    {
        var o = options.Value;
        var loginIdData = await SmartHomeRequestAsync("/v1/user/login/id/get",
            SmartHomeBody(new() { ["loginAccount"] = o.CloudAccount }), ct);
        var loginId = (string?)loginIdData?["loginId"] ?? throw new MideaCloudException("Cloud did not return a login ID.");

        var md5Once = Md5Hex(o.CloudPassword);
        var iamPassword = Sha256Hex(loginId + Md5Hex(md5Once) + SmartHomeLoginKey);
        var password = Sha256Hex(loginId + Sha256Hex(o.CloudPassword) + SmartHomeLoginKey);

        var loginBody = new JsonObject
        {
            ["data"] = new JsonObject { ["platform"] = 2, ["deviceId"] = AppDeviceId },
            ["iotData"] = new JsonObject
            {
                ["appId"] = SmartHomeAppId,
                ["src"] = SmartHomeAppId,
                ["clientType"] = 1,
                ["loginAccount"] = o.CloudAccount,
                ["iampwd"] = iamPassword,
                ["password"] = password,
                ["pushToken"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(90)).Replace('+', '-').Replace('/', '_'),
                ["stamp"] = Stamp(),
                ["reqId"] = RandomHex(16),
            },
        };
        var login = await SmartHomeRequestAsync("/mj/user/login", loginBody, ct);
        _smartHomeAccessToken = (string?)login?["mdata"]?["accessToken"]
            ?? throw new MideaCloudException("Cloud login did not return an access token.");
    }

    private static string RandomHex(int bytes) => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(bytes));

    private JsonObject SmartHomeBody(Dictionary<string, string> data)
    {
        var body = new JsonObject
        {
            ["appId"] = SmartHomeAppId,
            ["src"] = SmartHomeAppId,
            ["format"] = 2,
            ["clientType"] = 1,
            ["language"] = Language,
            ["deviceId"] = AppDeviceId,
            ["stamp"] = Stamp(),
            ["reqId"] = RandomHex(16),
        };
        foreach (var (k, v) in data) body[k] = v;
        return body;
    }

    private async Task<JsonNode?> SmartHomeRequestAsync(string endpoint, JsonObject body, CancellationToken ct)
    {
        var contents = body.ToJsonString();
        var random = RandomHex(16);
        var sign = Convert.ToHexStringLower(HMACSHA256.HashData(
            Encoding.ASCII.GetBytes(SmartHomeHmacKey), Encoding.ASCII.GetBytes(SmartHomeIotKey + contents + random)));

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{SmartHomeBaseUrl}/mas/v5/app/proxy?alias={endpoint}")
        {
            Content = new StringContent(contents, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("secretVersion", "1");
        request.Headers.Add("sign", sign);
        request.Headers.Add("random", random);
        request.Headers.Add("accessToken", _smartHomeAccessToken);

        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));

        var code = int.Parse(json?["code"]?.ToString() ?? "-1", CultureInfo.InvariantCulture);
        if (code != 0) throw new MideaCloudException($"Midea cloud error {code}: {(string?)json?["msg"]}", code);
        return json?["data"];
    }

    #endregion
}
