using System;
using System.Globalization;
using UnityEngine;

namespace AvatarPartAssembler.Editor.Authoring
{
    /// <summary>
    /// The color code the authoring window's candidate-color field reads and writes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a code and not a color wheel.</b> The candidate color is one exact <see cref="Color32"/> compared
    /// channel by channel against <c>Mesh.colors32</c>, so what the author needs is the ability to name that
    /// value exactly — the code a modelling tool, a texture report, or a teammate's note carries — not a picker
    /// whose float value has to be rounded back into the same space. The Unity color wheel is also an
    /// interaction: it emits a change for every intermediate value while it is dragged, which is the cost the
    /// window had to defer. A text field emits nothing until the author types a complete, valid code.
    /// </para>
    /// <para>
    /// <b>The canonical form is <c>#RRGGBB</c>.</b> It is what <see cref="Format"/> writes for an opaque color and
    /// what the field shows for the default <c>#000000</c>. <c>#RRGGBBAA</c> is also accepted, and is written
    /// back when the stored alpha is not opaque, so a value with a non-opaque alpha can be read, edited, and
    /// round-tripped without being silently rewritten to opaque. Six digits mean opaque alpha
    /// (<c>255</c>), because that is what a code without an alpha channel means everywhere else.
    /// </para>
    /// <para>
    /// <b>Parsing is strict.</b> The text must be the <c>#</c> prefix followed by exactly six or eight
    /// hexadecimal digits, optionally surrounded by whitespace. Nothing else is a color code: a bare
    /// <c>RRGGBB</c>, a three-digit shorthand, a named color, and a nine-digit value are all malformed and are
    /// reported as such instead of being guessed at. A guess would silently select a different candidate set than
    /// the author typed, which is worse than a validation message.
    /// </para>
    /// <para>
    /// <b>Malformed input keeps the previous color.</b> <see cref="TryApply"/> is the one place that rule lives:
    /// a valid code is committed (and reports whether it actually changed, so the caller invalidates the
    /// candidate cache exactly once), and a malformed code leaves the color exactly as it was. The candidate set
    /// is therefore never derived from a value the author is still typing, and never lost because of a typo.
    /// </para>
    /// <para>
    /// No Unity dependency beyond <see cref="Color32"/>, so the parsing, formatting, and retention rules are
    /// covered by pure tests.
    /// </para>
    /// </remarks>
    public static class ApaSeamColorCode
    {
        /// <summary>The code a fresh window shows: opaque black, the color a freshly imported mesh carries.</summary>
        public const string DefaultCode = "#000000";

        /// <summary>The prefix every code carries.</summary>
        public const char Prefix = '#';

        /// <summary>Digits of the canonical <c>#RRGGBB</c> form.</summary>
        public const int RgbDigits = 6;

        /// <summary>Digits of the accepted <c>#RRGGBBAA</c> form.</summary>
        public const int RgbaDigits = 8;

        /// <summary>
        /// The canonical code of a color: <c>#RRGGBB</c> when the alpha is opaque, <c>#RRGGBBAA</c> otherwise.
        /// </summary>
        /// <remarks>
        /// Upper-case digits, invariant culture, no shorthand: the field shows the same spelling the parser
        /// accepts, so a value that was read out of the field can be typed back into it unchanged.
        /// </remarks>
        public static string Format(Color32 color)
        {
            var rgb = Prefix
                      + Hex(color.r)
                      + Hex(color.g)
                      + Hex(color.b);

            return color.a == 255 ? rgb : rgb + Hex(color.a);
        }

        /// <summary>
        /// Parses a color code strictly.
        /// </summary>
        /// <param name="text">The typed text. Surrounding whitespace is ignored.</param>
        /// <param name="color">
        /// The parsed color, or <see cref="ApaSeamVertexColorCandidates.DefaultColor"/> when the text is
        /// malformed. Never a partial parse.
        /// </param>
        /// <returns>True when the text was a well-formed <c>#RRGGBB</c> or <c>#RRGGBBAA</c> code.</returns>
        public static bool TryParse(string text, out Color32 color)
        {
            color = ApaSeamVertexColorCandidates.DefaultColor;

            if (string.IsNullOrEmpty(text)) return false;

            var trimmed = text.Trim();
            if (trimmed.Length < 1 + RgbDigits) return false;
            if (trimmed[0] != Prefix) return false;

            var digits = trimmed.Substring(1);
            if (digits.Length != RgbDigits && digits.Length != RgbaDigits) return false;

            if (!TryParseByte(digits, 0, out var red)) return false;
            if (!TryParseByte(digits, 2, out var green)) return false;
            if (!TryParseByte(digits, 4, out var blue)) return false;

            byte alpha = 255;
            if (digits.Length == RgbaDigits && !TryParseByte(digits, 6, out alpha)) return false;

            color = new Color32(red, green, blue, alpha);
            return true;
        }

        /// <summary>
        /// Applies a typed code to the currently selected color.
        /// </summary>
        /// <param name="text">The typed text.</param>
        /// <param name="current">The color in effect before the edit.</param>
        /// <param name="applied">
        /// The color in effect after the edit: the parsed color for a valid code, and <paramref name="current"/>
        /// unchanged for malformed input.
        /// </param>
        /// <param name="changed">
        /// True when a valid code named a different color, which is exactly when the caller must invalidate the
        /// resolved seam candidates and the merge-check classification. False for malformed input and for a valid
        /// code that re-states the current color, so neither a typo nor a no-op edit re-resolves anything.
        /// </param>
        /// <returns>True when the text was a well-formed color code.</returns>
        public static bool TryApply(string text, Color32 current, out Color32 applied, out bool changed)
        {
            if (!TryParse(text, out var parsed))
            {
                applied = current;
                changed = false;
                return false;
            }

            applied = parsed;
            changed = !parsed.Equals(current);
            return true;
        }

        private static string Hex(byte value)
        {
            return value.ToString("X2", CultureInfo.InvariantCulture);
        }

        private static bool TryParseByte(string digits, int offset, out byte value)
        {
            value = 0;

            var high = HexDigit(digits[offset]);
            var low = HexDigit(digits[offset + 1]);
            if (high < 0 || low < 0) return false;

            value = (byte)((high << 4) | low);
            return true;
        }

        private static int HexDigit(char character)
        {
            if (character >= '0' && character <= '9') return character - '0';
            if (character >= 'a' && character <= 'f') return character - 'a' + 10;
            if (character >= 'A' && character <= 'F') return character - 'A' + 10;
            return -1;
        }
    }
}
