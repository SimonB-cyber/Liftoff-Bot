using BepInEx.Configuration;

namespace LiftoffPhotonEventLogger.Features.Competition;

internal sealed class CompetitionConfig
{
    public ConfigEntry<bool>   Enabled            { get; }
    public ConfigEntry<string> ServerUrl          { get; }
    public ConfigEntry<string> ApiKey             { get; }
    public ConfigEntry<int>    ReconnectDelaySecs { get; }

    public CompetitionConfig(ConfigFile config)
    {
        const string section = "Competition";

        Enabled = config.Bind(section, "Enabled", false,
            "Enable the competition server connection.");

        ServerUrl = config.Bind(section, "ServerUrl", "ws://localhost:3000/ws/plugin",
            "WebSocket URL of the competition server.");

        ApiKey = config.Bind(section, "ApiKey", "",
            "API key sent in the Authorization header when connecting. Must match PLUGIN_API_KEY on the server.");

        ReconnectDelaySecs = config.Bind(section, "ReconnectDelaySecs", 5,
            new ConfigDescription("Seconds to wait before reconnecting after a dropped connection.", new AcceptableValueRange<int>(1, 60)));
    }
}
