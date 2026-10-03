namespace HADA.Engine.WebSocket;

/// <summary>
/// What Home Assistant's <c>mobile_app</c> integration gave this computer when it registered there: the id of the
/// webhook it answers on, which is a secret, since whoever knows it can speak for the computer.
/// </summary>
/// <param name="BaseUrl">The Home Assistant it was registered with; another address is another Home Assistant.</param>
/// <param name="DeviceId">The device id it was registered under; Home Assistant keeps one registration per id.</param>
public sealed record MobileAppRegistration(string WebhookId, string BaseUrl, string DeviceId)
{
    // Never let the webhook id end up in logs through the record's generated ToString.
    public override string ToString() => $"MobileAppRegistration {{ BaseUrl = {BaseUrl}, DeviceId = {DeviceId} }}";
}

/// <summary>
/// Keeps the registrations between runs, one per Home Assistant, by the <see cref="HaWebSocketOptions.Id"/> of its
/// settings. Not part of the settings themselves: an engine is restarted whenever its settings change, and it is
/// the engine that writes this.
/// </summary>
public interface IMobileAppRegistrationStore
{
    MobileAppRegistration? Load(string serverId);

    /// <param name="registration"><see langword="null"/> to forget the registration.</param>
    void Save(string serverId, MobileAppRegistration? registration);
}

/// <summary>Remembers registrations for as long as the process runs; for tests and for hosts without a folder to write to.</summary>
public sealed class InMemoryMobileAppRegistrationStore : IMobileAppRegistrationStore
{
    private readonly Dictionary<string, MobileAppRegistration> _registrations = new(StringComparer.Ordinal);

    public MobileAppRegistration? Load(string serverId)
    {
        lock (_registrations)
        {
            return _registrations.GetValueOrDefault(serverId);
        }
    }

    public void Save(string serverId, MobileAppRegistration? registration)
    {
        lock (_registrations)
        {
            if (registration is null)
            {
                _registrations.Remove(serverId);
            }
            else
            {
                _registrations[serverId] = registration;
            }
        }
    }
}
