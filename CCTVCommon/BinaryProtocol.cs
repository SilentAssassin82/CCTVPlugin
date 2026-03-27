using System;
using System.IO;
using System.Text;

namespace CCTVCommon
{
    /// <summary>
    /// Shared binary framing protocol used between CCTVCapture.exe and the Torch plugin.
    ///
    /// Every post-handshake message on the wire uses this frame layout:
    ///   [4 bytes: message type, uint32 little-endian]
    ///   [4 bytes: payload length N, uint32 little-endian]
    ///   [N bytes: payload]
    ///
    /// The text handshake (HELLO / AUTH) is still plain UTF-8 newline-terminated lines
    /// sent before the binary protocol starts — it is NOT wrapped in frames.
    /// </summary>
    public static class BinaryProtocol
    {
        // ── Message type IDs ──────────────────────────────────────────────────────
        // MSG_TEXT: payload is the UTF-8 text of the message (no trailing \n).
        // Used for all control messages in both directions (PING, PONG, CONFIG, etc.).
        public const uint MSG_TEXT  = 1;

        // MSG_FRAME: client → server, binary FRAME payload.
        //   Payload layout: [4B width uint32][4B height uint32][1B flags][raw gzip bytes]
        public const uint MSG_FRAME = 100;

        // MSG_QUAD: client → server, binary QUAD payload.
        //   Payload layout: [1B quadrant id (0-3)][1B color flag (0/1)][raw gzip bytes]
        public const uint MSG_QUAD  = 101;

        // ── FRAME flags (1-byte bitmask in MSG_FRAME payload) ────────────────────
        public const byte FLAG_COLOR = 0x01;   // set = COLOR, clear = GRAY
        public const byte FLAG_GZ    = 0x02;   // set = gzip compressed (always set currently)

        // ── Quadrant IDs (1-byte value in MSG_QUAD payload) ─────────────────────
        public const byte QUAD_TL = 0;
        public const byte QUAD_TR = 1;
        public const byte QUAD_BL = 2;
        public const byte QUAD_BR = 3;

        // ── Safety cap ──────────────────────────────────────────────────────────
        private const int MAX_PAYLOAD_BYTES = 8 * 1024 * 1024; // 8 MB

        // ── Low-level I/O helpers ────────────────────────────────────────────────

        /// <summary>
        /// Reads exactly <paramref name="count"/> bytes into <paramref name="buf"/>
        /// starting at <paramref name="offset"/>, blocking until all bytes arrive.
        /// Throws <see cref="EndOfStreamException"/> if the stream closes mid-read.
        /// </summary>
        public static void ReadExactly(Stream s, byte[] buf, int offset, int count)
        {
            while (count > 0)
            {
                int n = s.Read(buf, offset, count);
                if (n == 0) throw new EndOfStreamException("Connection closed mid-frame");
                offset += n;
                count  -= n;
            }
        }

        /// <summary>
        /// Reads an 8-byte binary frame header and returns the message type and payload length.
        /// </summary>
        public static (uint type, int length) ReadHeader(Stream s)
        {
            byte[] hdr = new byte[8];
            ReadExactly(s, hdr, 0, 8);
            uint type = BitConverter.ToUInt32(hdr, 0);
            uint len  = BitConverter.ToUInt32(hdr, 4);
            if (len > MAX_PAYLOAD_BYTES)
                throw new InvalidDataException($"Binary frame too large: {len} bytes (type {type})");
            return (type, (int)len);
        }

        /// <summary>
        /// Writes a complete binary frame to the stream.
        /// </summary>
        public static void WriteFrame(Stream s, uint type, byte[] payload, int offset, int count)
        {
            byte[] hdr = new byte[8];
            hdr[0] = (byte)( type        & 0xFF);
            hdr[1] = (byte)((type >>  8) & 0xFF);
            hdr[2] = (byte)((type >> 16) & 0xFF);
            hdr[3] = (byte)((type >> 24) & 0xFF);
            uint len = (uint)count;
            hdr[4] = (byte)( len        & 0xFF);
            hdr[5] = (byte)((len >>  8) & 0xFF);
            hdr[6] = (byte)((len >> 16) & 0xFF);
            hdr[7] = (byte)((len >> 24) & 0xFF);
            s.Write(hdr, 0, 8);
            if (count > 0)
                s.Write(payload, offset, count);
        }

        /// <summary>
        /// Writes a MSG_TEXT binary frame carrying <paramref name="message"/> as UTF-8.
        /// </summary>
        public static void WriteTextFrame(Stream s, string message)
        {
            byte[] payload = Encoding.UTF8.GetBytes(message);
            WriteFrame(s, MSG_TEXT, payload, 0, payload.Length);
        }

        /// <summary>
        /// Builds a complete MSG_TEXT binary frame as a byte[] suitable for queuing.
        /// </summary>
        public static byte[] CreateTextFrame(string message)
        {
            byte[] payload = Encoding.UTF8.GetBytes(message);
            byte[] frame   = new byte[8 + payload.Length];
            uint type = MSG_TEXT;
            uint len  = (uint)payload.Length;
            frame[0] = (byte)( type        & 0xFF);
            frame[1] = (byte)((type >>  8) & 0xFF);
            frame[2] = (byte)((type >> 16) & 0xFF);
            frame[3] = (byte)((type >> 24) & 0xFF);
            frame[4] = (byte)( len        & 0xFF);
            frame[5] = (byte)((len >>  8) & 0xFF);
            frame[6] = (byte)((len >> 16) & 0xFF);
            frame[7] = (byte)((len >> 24) & 0xFF);
            Buffer.BlockCopy(payload, 0, frame, 8, payload.Length);
            return frame;
        }
    }
}
