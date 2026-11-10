namespace TransitPass.Network.Api.Stations;

/// <summary>A metro station. A record is a small class whose job is to hold data.</summary>
/// <param name="Id">Stable, URL-friendly id.</param>
/// <param name="Name">Display name.</param>
public sealed record Station(string Id, string Name);
