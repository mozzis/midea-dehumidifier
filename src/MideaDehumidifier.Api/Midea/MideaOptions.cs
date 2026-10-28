namespace MideaDehumidifier.Api.Midea;

public sealed class MideaOptions
{
    public const string SectionName = "Midea";

    /// <summary>IP address or host name of the dehumidifier.</summary>
    public string Host { get; set; } = "";

    public int Port { get; set; } = 6444;

    /// <summary>Appliance ID. 0 means discover it from the device over UDP and cache it.</summary>
    public long DeviceId { get; set; }

    /// <summary>Optional hex V3 token. If Token and Key are both empty they are fetched from the Midea cloud once and cached.</summary>
    public string Token { get; set; } = "";

    /// <summary>Optional hex V3 key; see <see cref="Token"/>.</summary>
    public string Key { get; set; } = "";

    /// <summary>Which Midea app account the device is registered with: <c>NetHomePlus</c> or <c>SmartHome</c>.</summary>
    public string Cloud { get; set; } = "NetHomePlus";

    public string CloudRegion { get; set; } = "US";
    public string CloudAccount { get; set; } = "";
    public string CloudPassword { get; set; } = "";

    public int PollIntervalSeconds { get; set; } = 60;
    public int StaleAfterSeconds { get; set; } = 300;

    /// <summary>SQLite file. A relative path is resolved against the application content root.</summary>
    public string DatabasePath { get; set; } = "midea.db";
}
