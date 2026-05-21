using System;
using System.Globalization;

namespace LocoNet.Net;

/// <summary>
/// Line-by-line parser for the JMRI <c>LocoNetOverTcp</c> ASCII wire format.
/// Each line is of the form <c>SEND xx xx xx</c> or <c>RECEIVE xx xx xx</c> with
/// 2-digit hexadecimal bytes separated by whitespace. Comments after <c>#</c> are ignored.
/// </summary>
/// <remarks>
/// This parser is allocation-free for ordinary lines (it operates on a <see cref="ReadOnlySpan{Char}"/>),
/// and returns a fully-validated <see cref="LnMsg"/> via <see cref="LnMsg.FromBytes"/> so callers
/// receive only length- and checksum-correct frames.
/// </remarks>
public static class JmriLineParser
{
    /// <summary>Kind of JMRI LocoNet line.</summary>
    public enum LineKind
    {
        /// <summary>Line was empty, a comment, or otherwise contained no frame.</summary>
        None,
        /// <summary>An outgoing <c>SEND</c> line (typically client → server).</summary>
        Send,
        /// <summary>An incoming <c>RECEIVE</c> line (typically server → client).</summary>
        Receive,
    }

    /// <summary>
    /// Try to parse a single JMRI LocoNetOverTcp line.
    /// </summary>
    /// <param name="line">The line, without the terminating CR/LF.</param>
    /// <param name="kind">The line kind on success.</param>
    /// <param name="message">The parsed and validated <see cref="LnMsg"/> on success.</param>
    /// <returns><see langword="true"/> if a complete frame was parsed.</returns>
    public static bool TryParse(ReadOnlySpan<char> line, out LineKind kind, out LnMsg message)
    {
        kind = LineKind.None;
        message = default;

        // Strip comments.
        int hash = line.IndexOf('#');
        if (hash >= 0) line = line[..hash];
        line = line.Trim();
        if (line.IsEmpty) return false;

        // Identify keyword.
        int firstSpace = line.IndexOfAny(' ', '\t');
        if (firstSpace <= 0) return false;
        var keyword = line[..firstSpace];

        LineKind detected;
        if (keyword.Equals("SEND", StringComparison.OrdinalIgnoreCase))
        {
            detected = LineKind.Send;
        }
        else if (keyword.Equals("RECEIVE", StringComparison.OrdinalIgnoreCase))
        {
            detected = LineKind.Receive;
        }
        else
        {
            return false;
        }

        var rest = line[(firstSpace + 1)..].Trim();
        if (rest.IsEmpty) return false;

        Span<byte> buffer = stackalloc byte[16]; // largest LocoNet frame
        int written = 0;

        while (!rest.IsEmpty)
        {
            int sep = rest.IndexOfAny(' ', '\t');
            ReadOnlySpan<char> token = sep < 0 ? rest : rest[..sep];
            rest = sep < 0 ? default : rest[(sep + 1)..].TrimStart();

            if (token.IsEmpty) continue;
            if (token.Length != 2) return false;
            if (!byte.TryParse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
            {
                return false;
            }
            if (written >= buffer.Length) return false;
            buffer[written++] = b;
        }

        if (written < 2) return false;

        try
        {
            message = LnMsg.FromBytes(buffer[..written]);
            kind = detected;
            return true;
        }
        catch (ArgumentException)
        {
            // Bad length or checksum.
            return false;
        }
    }

    /// <summary>
    /// Format an outgoing <c>SEND</c> line (no terminator appended).
    /// </summary>
    public static string FormatSend(LnMsg message) => Format("SEND", message);

    /// <summary>
    /// Format an outgoing <c>RECEIVE</c> line (no terminator appended). Useful when implementing
    /// a server-side mirror.
    /// </summary>
    public static string FormatReceive(LnMsg message) => Format("RECEIVE", message);

    private static string Format(string keyword, LnMsg message)
    {
        var bytes = message.Bytes;
        // keyword + ' ' + N * "XX " (drop trailing space)
        int len = keyword.Length + 1 + bytes.Length * 3 - 1;
        return string.Create(len, (keyword, message), static (span, state) =>
        {
            var (kw, m) = state;
            kw.AsSpan().CopyTo(span);
            int pos = kw.Length;
            span[pos++] = ' ';
            var bs = m.Bytes;
            for (int i = 0; i < bs.Length; i++)
            {
                if (i > 0) span[pos++] = ' ';
                byte b = bs[i];
                span[pos++] = ToHex((byte)(b >> 4));
                span[pos++] = ToHex((byte)(b & 0x0F));
            }
        });

        static char ToHex(byte nibble) => (char)(nibble < 10 ? '0' + nibble : 'A' + (nibble - 10));
    }
}
