using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

static class LanInviteCode
{
    const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    internal const int TokenSize = 12;
    const int PayloadSize = TokenSize + 5;
    const int CodeLength = 28;

    internal static string Secret(byte[] token) {
        using (var sha = SHA256.Create()) return Network.Hex(sha.ComputeHash(token));
    }

    internal static string Encode(byte[] token, int gamePort, int infoPort) {
        var bytes = new byte[PayloadSize];
        Array.Copy(token, bytes, TokenSize);
        bytes[TokenSize] = (byte)(gamePort >> 8);
        bytes[TokenSize + 1] = (byte)gamePort;
        bytes[TokenSize + 2] = (byte)(infoPort >> 8);
        bytes[TokenSize + 3] = (byte)infoPort;
        bytes[PayloadSize - 1] = Checksum(bytes);
        var code = new StringBuilder(CodeLength + 6);
        int buffer = 0, bits = 0, written = 0;
        foreach (var value in bytes) {
            buffer = (buffer << 8) | value;
            bits += 8;
            while (bits >= 5) {
                bits -= 5;
                if (written > 0 && written % 4 == 0) code.Append('-');
                code.Append(Alphabet[(buffer >> bits) & 31]);
                written++;
            }
        }
        code.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return code.ToString();
    }

    internal static LanRooms.Invitation Decode(string input) {
        var code = input.Where(ch => ch != '-' && !Char.IsWhiteSpace(ch)).ToArray();
        if (code.Length != CodeLength) throw new FormatException();
        var bytes = new byte[PayloadSize];
        int buffer = 0, bits = 0, written = 0;
        foreach (var character in code) {
            var ch = Char.ToUpperInvariant(character);
            if (ch == 'O') ch = '0';
            if (ch == 'I' || ch == 'L') ch = '1';
            var value = Alphabet.IndexOf(ch);
            if (value < 0) throw new FormatException();
            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits < 8) continue;
            bits -= 8;
            bytes[written++] = (byte)(buffer >> bits);
        }
        var gamePort = (bytes[TokenSize] << 8) | bytes[TokenSize + 1];
        var infoPort = (bytes[TokenSize + 2] << 8) | bytes[TokenSize + 3];
        if ((buffer & 15) != 0 || Checksum(bytes) != bytes[PayloadSize - 1] || gamePort == 0 || infoPort == 0 || gamePort == infoPort)
            throw new FormatException();
        var token = bytes.Take(TokenSize).ToArray();
        return new LanRooms.Invitation { Secret = Secret(token), Node = LanTunnel.PublicNodes[0], PublicPeers = true,
            Token = token, Port = gamePort, InfoPort = infoPort };
    }

    static byte Checksum(byte[] bytes) {
        using (var sha = SHA256.Create()) return sha.ComputeHash(bytes, 0, PayloadSize - 1)[0];
    }
}
