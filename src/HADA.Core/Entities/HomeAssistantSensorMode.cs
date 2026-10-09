namespace HADA.Core.Entities;

/// <summary>How sensors reach a Home Assistant the computer is connected to directly, if at all.</summary>
public enum HomeAssistantSensorMode
{
    /// <summary>Not through this connection: Home Assistant gets them through MQTT, or is only there for notifications.</summary>
    Off,

    /// <summary>
    /// As states written through the REST API, which need an administrator's token. They have no unique id, cannot
    /// be managed in Home Assistant, and are gone when it restarts. What connections did before there was a choice.
    /// </summary>
    States,

    /// <summary>
    /// As entities of the device the computer is to Home Assistant's <c>mobile_app</c> integration: with a unique
    /// id, kept across restarts, and with the token of any user.
    /// </summary>
    Entities,

    /// <summary>
    /// Through the HADA integration for Home Assistant, which has to be installed there: as a device of its own,
    /// whose entities follow the computer's, are removed when the computer no longer has them, and are
    /// unavailable the moment it goes away.
    /// </summary>
    Integration,
}
