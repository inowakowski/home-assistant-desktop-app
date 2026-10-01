namespace HADA.Core.Abstractions;

/// <summary>Decides which registered entities are exposed to Home Assistant.</summary>
public interface IEntityFilter
{
    bool IsEnabled(string entityId);
}
