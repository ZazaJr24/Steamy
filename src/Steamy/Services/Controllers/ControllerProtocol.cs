// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Steamy contributors. Shared wire contract; no DS4Windows code.
#nullable enable
using System.Buffers.Binary;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Steamy.ControllerProtocol;

public sealed record ControllerInput(float LX, float LY, float RX, float RY, float LT, float RT, uint Buttons);
public sealed record ControllerDevice(int Slot, string Id, string Name, string Connection, int Battery, bool Charging, ControllerInput Input);
public sealed record ControllerProfile(string Name = "Default", int LeftDeadzone = 10, int RightDeadzone = 10,
    string Curve = "Linear", byte Red = 96, byte Green = 105, byte Blue = 255, Dictionary<string, string>? Mapping = null);
public sealed record ControllerState(bool Running, string Status, string DriverVersion, bool HidHideInstalled, IReadOnlyList<ControllerDevice> Devices);
public sealed record ControllerRequest(int Version, long Id, string Method, string? Secret = null, int Slot = 0,
    ControllerProfile? Profile = null, byte Heavy = 0, byte Light = 0);
public sealed record ControllerResponse(int Version, long Id, bool Ok, string Error, ControllerState? State = null);
public static class ControllerWire
{
    public const int Version = 1, MaxFrameBytes = 65536;
    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken token)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(value);
        if (body.Length > MaxFrameBytes) throw new InvalidDataException("Controller frame is too large.");
        var length = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(length, body.Length);
        await stream.WriteAsync(length, token); await stream.WriteAsync(body, token); await stream.FlushAsync(token);
    }
    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        var length = new byte[4]; await stream.ReadExactlyAsync(length, token);
        var size = BinaryPrimitives.ReadInt32LittleEndian(length);
        if (size is <= 0 or > MaxFrameBytes) throw new InvalidDataException("Invalid controller frame length.");
        var body = new byte[size]; await stream.ReadExactlyAsync(body, token);
        return JsonSerializer.Deserialize<T>(body, new JsonSerializerOptions { MaxDepth = 12 }) ?? throw new InvalidDataException("Empty controller frame.");
    }
}
