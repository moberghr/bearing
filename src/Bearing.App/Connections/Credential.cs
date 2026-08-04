using System;

namespace Bearing.App.Connections;

/// <summary>A resolved secret ready to hand to the provider as the connection password, plus an optional
/// expiry. <see cref="ExpiresAt"/> is set for short-lived credentials (Entra tokens) and null for a fixed
/// password / prompted password — it drives proactive disconnect-before-expiry in
/// <see cref="ConnectionSessionManager"/>.</summary>
public sealed record Credential(string? Secret, DateTimeOffset? ExpiresAt);
