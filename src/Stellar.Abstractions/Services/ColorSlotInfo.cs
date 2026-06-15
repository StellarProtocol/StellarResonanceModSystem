// src/Stellar.Abstractions/Services/ColorSlotInfo.cs
namespace Stellar.Abstractions.Services;

/// <summary>Editor enumeration record for one registered slot.</summary>
public sealed record ColorSlotInfo(string Key, string Owner, string Label);
