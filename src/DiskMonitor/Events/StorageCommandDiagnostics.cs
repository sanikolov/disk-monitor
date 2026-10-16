using System.Globalization;

namespace DiskMonitor.Events;

internal static class StorageCommandDiagnostics
{
    // Windows SDK scsi.h and srb.h define these values. Unknown values remain
    // numeric; neither NOT READY nor a frozen queue establishes a root cause.
    public static string Describe(string description, IReadOnlyDictionary<string, string> values)
    {
        var decoded = new List<string>();
        if (values.TryGetValue("CdbBytes", out var cdbText))
        {
            var cdb = cdbText.Trim();
            if (cdb.Length >= 2 && cdb.Length % 2 == 0 && cdb.All(Uri.IsHexDigit))
            {
                var opcode = byte.Parse(cdb.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                var command = opcode switch
                {
                    0x00 => "TEST UNIT READY",
                    0x03 => "REQUEST SENSE",
                    0x12 => "INQUIRY",
                    0x1A => "MODE SENSE (6)",
                    0x1B => "START STOP UNIT",
                    0x25 => "READ CAPACITY (10)",
                    0x28 => "READ (10)",
                    0x2A => "WRITE (10)",
                    0x4D => "LOG SENSE",
                    0x5A => "MODE SENSE (10)",
                    0x9E when cdb.Length >= 4 && (byte.Parse(cdb.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) & 0x1F) == 0x10 => "READ CAPACITY (16)",
                    _ => "unknown command"
                };
                decoded.Add($"Command: {command} (opcode 0x{opcode:X2}).");
                if (ReadNumber(values, "CdbByteCount") is { } count && count != cdb.Length / 2)
                {
                    decoded.Add("CDB length differs from CdbByteCount; the recorded command may be incomplete.");
                }
            }
            else
            {
                decoded.Add("Command: unavailable (empty or malformed CDB).");
            }
        }

        var srb = ReadNumber(values, "SrbStatus");
        if (srb is { } status)
        {
            var baseStatus = status & 0x3F;
            var name = baseStatus switch
            {
                0x01 => "SUCCESS",
                0x04 => "ERROR",
                0x05 => "BUSY",
                0x06 => "INVALID REQUEST",
                0x08 => "NO DEVICE",
                0x09 => "TIMEOUT",
                0x0A => "SELECTION TIMEOUT",
                0x0B => "COMMAND TIMEOUT",
                0x0E => "BUS RESET",
                0x24 => "NOT POWERED",
                _ => "unknown status"
            };
            decoded.Add($"SRB: {name} (0x{baseStatus:X2}); autosense valid={(status & 0x80) != 0}; queue frozen={(status & 0x40) != 0}.");
        }

        if (ReadNumber(values, "ScsiStatus") is { } scsi)
        {
            var name = scsi switch { 0 => "GOOD", 2 => "CHECK CONDITION", 8 => "BUSY", _ => "unknown status" };
            decoded.Add($"SCSI: {name} (0x{scsi:X2}).");
        }

        if (ReadNumber(values, "SenseKey") is { } key)
        {
            var name = key switch
            {
                0 => "NO SENSE", 1 => "RECOVERED ERROR", 2 => "NOT READY", 3 => "MEDIUM ERROR",
                4 => "HARDWARE ERROR", 5 => "ILLEGAL REQUEST", 6 => "UNIT ATTENTION", 7 => "DATA PROTECT",
                0xB => "ABORTED COMMAND", _ => "unknown sense key"
            };
            decoded.Add($"Sense: {name} (0x{key:X2}); ASC={Hex(ReadNumber(values, "AdditionalSenseCode"))}; ASCQ={Hex(ReadNumber(values, "AdditionalSenseCodeQualifier"))}.");
            if (srb is null || (srb.Value & 0x80) == 0)
            {
                decoded.Add("Sense data is unverified: SRB autosense validity was not established.");
            }
            else if (key == 2 && ReadNumber(values, "AdditionalSenseCode") == 4)
            {
                var qualifier = ReadNumber(values, "AdditionalSenseCodeQualifier") switch
                {
                    0 => "Logical unit not ready; cause not reportable. This does not distinguish sleep/spin-up, power, connection, enclosure, or drive faults.",
                    1 => "Logical unit is becoming ready.",
                    2 => "Logical unit not ready; initializing command required.",
                    3 => "Logical unit not ready; manual intervention required.",
                    4 => "Logical unit not ready; format in progress.",
                    _ => "Logical unit not ready; qualifier unavailable or not decoded."
                };
                decoded.Add(qualifier);
            }
        }

        foreach (var field in new[] { "DownLevelIrpStatus", "IrpStatus", "NTStatus" })
        {
            if (ReadNumber(values, field) is not { } ntStatus)
            {
                continue;
            }

            var name = ntStatus switch
            {
                0xC0000185 => "STATUS_IO_DEVICE_ERROR",
                0xC00000A3 => "STATUS_DEVICE_NOT_READY",
                0xC000000D => "STATUS_INVALID_PARAMETER",
                0xC00000BB => "STATUS_NOT_SUPPORTED",
                0xC00000B5 => "STATUS_IO_TIMEOUT",
                _ => "unknown NTSTATUS"
            };
            decoded.Add($"{field}: {name} (0x{ntStatus:X8}).");
        }

        // Preserve every payload field, including ASCQ, IRP status, IOCTL, LBA,
        // transfer size, firmware and future fields absent from today's decoder.
        var payload = string.Join("; ", values.Select(pair => $"{pair.Key}={pair.Value.Trim()}"));
        return string.Join(Environment.NewLine, new[] { description }.Concat(decoded).Append(payload));
    }

    private static string Hex(uint? value) => value is { } number ? $"0x{number:X2}" : "unavailable";

    private static uint? ReadNumber(IReadOnlyDictionary<string, string> values, string field)
    {
        if (!values.TryGetValue(field, out var text))
        {
            return null;
        }

        text = text.Trim();
        var hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        return uint.TryParse(hex ? text.AsSpan(2) : text.AsSpan(), hex ? NumberStyles.HexNumber : NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var value) ? value : null;
    }
}
