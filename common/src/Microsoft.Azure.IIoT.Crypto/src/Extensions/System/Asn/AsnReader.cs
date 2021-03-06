// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace System.Security.Cryptography.Asn1 {
    using System.Buffers;
    using System.Buffers.Binary;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Numerics;
    using System.Runtime.InteropServices;
    using System.Text;

    internal static class SR {
        public const string Cryptography_Der_Invalid_Encoding = "ASN1 corrupted data.";
        public const string Cryptography_Asn_NamedBitListRequiresFlagsEnum = "Named bit list operations require an enum with the [Flags] attribute.";
        public const string Cryptography_Asn_EnumeratedValueRequiresNonFlagsEnum = "ASN.1 Enumerated values only apply to enum types without the [Flags] attribute.";
        public const string Cryptography_Asn_NamedBitListValueTooBig = "The encoded named bit list value is larger than the value size of the '{0}' enum.";
        public const string Cryptography_Asn_UniversalValueIsFixed = "Tags with TagClass Universal must have the appropriate TagValue value for the data type being read or written.";
        public const string Argument_InvalidOidValue = "The OID value was invalid.";
        public const string Cryptography_AsnWriter_EncodeUnbalancedStack = "Encode cannot be called while a Sequence or SetOf is still open.";
        public const string Cryptography_AsnWriter_PopWrongTag = "Cannot pop the requested tag as it is not currently in progress.";
        public const string Cryptography_Asn_UnusedBitCountRange = "Unused bit count must be between 0 and 7, inclusive.";
        public const string Cryptography_WriteEncodedValue_OneValueAtATime = "The input to WriteEncodedValue must represent a single encoded value with no trailing data.";
    }

    internal class AsnReader {
        // T-REC-X.690-201508 sec 9.2
        internal const int MaxCERSegmentSize = 1000;

        // T-REC-X.690-201508 sec 8.1.5 says only 0000 is legal.
        private const int kEndOfContentsEncodedLength = 2;

        private ReadOnlyMemory<byte> _data;
        private readonly AsnEncodingRules _ruleSet;

        public bool HasData => !_data.IsEmpty;

        public AsnReader(ReadOnlyMemory<byte> data, AsnEncodingRules ruleSet) {
            CheckEncodingRules(ruleSet);

            _data = data;
            _ruleSet = ruleSet;
        }

        public void ThrowIfNotEmpty() {
            if (HasData) {
                throw new CryptographicException(SR.Cryptography_Der_Invalid_Encoding);
            }
        }

        public static bool TryPeekTag(ReadOnlySpan<byte> source, out Asn1Tag tag, out int bytesRead) {
            return Asn1Tag.TryParse(source, out tag, out bytesRead);
        }

        public Asn1Tag PeekTag() {
            if (TryPeekTag(_data.Span, out var tag, out var bytesRead)) {
                return tag;
            }

            throw new CryptographicException(SR.Cryptography_Der_Invalid_Encoding);
        }

        private static bool TryReadLength(
            ReadOnlySpan<byte> source,
            AsnEncodingRules ruleSet,
            out int? length,
            out int bytesRead) {
            length = null;
            bytesRead = 0;

            CheckEncodingRules(ruleSet);

            if (source.IsEmpty) {
                return false;
            }

            // T-REC-X.690-201508 sec 8.1.3

            var lengthOrLengthLength = source[bytesRead];
            bytesRead++;
            const byte MultiByteMarker = 0x80;

            // 0x00-0x7F are direct length values.
            // 0x80 is BER/CER indefinite length.
            // 0x81-0xFE says that the length takes the next 1-126 bytes.
            // 0xFF is forbidden.
            if (lengthOrLengthLength == MultiByteMarker) {
                // T-REC-X.690-201508 sec 10.1 (DER: Length forms)
                if (ruleSet == AsnEncodingRules.DER) {
                    bytesRead = 0;
                    return false;
                }

                // Null length == indefinite.
                return true;
            }

            if (lengthOrLengthLength < MultiByteMarker) {
                length = lengthOrLengthLength;
                return true;
            }

            if (lengthOrLengthLength == 0xFF) {
                bytesRead = 0;
                return false;
            }

            var lengthLength = (byte)(lengthOrLengthLength & ~MultiByteMarker);

            // +1 for lengthOrLengthLength
            if (lengthLength + 1 > source.Length) {
                bytesRead = 0;
                return false;
            }

            // T-REC-X.690-201508 sec 9.1 (CER: Length forms)
            // T-REC-X.690-201508 sec 10.1 (DER: Length forms)
            var minimalRepresentation =
                ruleSet == AsnEncodingRules.DER || ruleSet == AsnEncodingRules.CER;

            // The ITU-T specifications tecnically allow lengths up to ((2^128) - 1), but
            // since Span's length is a signed Int32 we're limited to identifying memory
            // that is within ((2^31) - 1) bytes of the tag start.
            if (minimalRepresentation && lengthLength > sizeof(int)) {
                bytesRead = 0;
                return false;
            }

            uint parsedLength = 0;

            for (var i = 0; i < lengthLength; i++) {
                var current = source[bytesRead];
                bytesRead++;

                if (parsedLength == 0) {
                    if (minimalRepresentation && current == 0) {
                        bytesRead = 0;
                        return false;
                    }

                    if (!minimalRepresentation && current != 0) {
                        // Under BER rules we could have had padding zeros, so
                        // once the first data bits come in check that we fit within
                        // sizeof(int) due to Span bounds.

                        if (lengthLength - i > sizeof(int)) {
                            bytesRead = 0;
                            return false;
                        }
                    }
                }

                parsedLength <<= 8;
                parsedLength |= current;
            }

            // This value cannot be represented as a Span length.
            if (parsedLength > int.MaxValue) {
                bytesRead = 0;
                return false;
            }

            if (minimalRepresentation && parsedLength < MultiByteMarker) {
                bytesRead = 0;
                return false;
            }

            Debug.Assert(bytesRead > 0);
            length = (int)parsedLength;
            return true;
        }

        internal Asn1Tag ReadTagAndLength(out int? contentsLength, out int bytesRead) {
            if (TryPeekTag(_data.Span, out var tag, out var tagBytesRead) &&
                TryReadLength(_data.Slice(tagBytesRead).Span, _ruleSet, out var length, out var lengthBytesRead)) {
                var allBytesRead = tagBytesRead + lengthBytesRead;

                if (tag.IsConstructed) {
                    // T-REC-X.690-201508 sec 9.1 (CER: Length forms) says constructed is always indefinite.
                    if (_ruleSet == AsnEncodingRules.CER && length != null) {
                        throw new CryptographicException(SR.Cryptography_Der_Invalid_Encoding);
                    }
                }
                else if (length == null) {
                    // T-REC-X.690-201508 sec 8.1.3.2 says primitive encodings must use a definite form.
                    throw new CryptographicException(SR.Cryptography_Der_Invalid_Encoding);
                }

                bytesRead = allBytesRead;
                contentsLength = length;
                return tag;
            }

            throw new CryptographicException(SR.Cryptography_Der_Invalid_Encoding);
        }

        private static void ValidateEndOfContents(Asn1Tag tag, int? length, int headerLength) {
            // T-REC-X.690-201508 sec 8.1.5 excludes the BER 8100 length form for 0.
            if (tag.IsConstructed || length != 0 || headerLength != kEndOfContentsEncodedLength) {
                throw new CryptographicException(SR.Cryptography_Der_Invalid_Encoding);
            }
        }

        /// <summary>
        /// Get the number of bytes between the start of <paramref name="source" /> and
        /// the End-of-Contents marker
        /// </summary>
        private int SeekEndOfContents(ReadOnlyMemory<byte> source) {
            var cur = source;
            var totalLen = 0;

            var tmpReader = new AsnReader(cur, _ruleSet);
            // Our reader is bounded by int.MaxValue.
            // The most aggressive data input would be a one-byte tag followed by
            // indefinite length "ad infinitum", which would be half the input.
            // So the depth marker can never overflow the signed integer space.
            var depth = 1;

            while (tmpReader.HasData) {
                var tag = tmpReader.ReadTagAndLength(out var length, out var bytesRead);

                if (tag == Asn1Tag.EndOfContents) {
                    ValidateEndOfContents(tag, length, bytesRead);

                    depth--;

                    if (depth == 0) {
                        // T-REC-X.690-201508 sec 8.1.1.1 / 8.1.1.3 indicate that the
                        // End-of-Contents octets are "after" the contents octets, not
                        // "at the end" of them, so we don't include these bytes in the
                        // accumulator.
                        return totalLen;
                    }
                }

                // We found another indefinite length, that means we need to find another
                // EndOfContents marker to balance it out.
                if (length == null) {
                    depth++;
                    tmpReader._data = tmpReader._data.Slice(bytesRead);
                    totalLen += bytesRead;
                }
                else {
                    // This will throw a CryptographicException if the length exceeds our bounds.
                    var tlv = Slice(tmpReader._data, 0, bytesRead + length.Value);

                    // No exception? Then slice the data and continue.
                    tmpReader._data = tmpReader._data.Slice(tlv.Length);
                    totalLen += tlv.Length;
                }
            }

            throw new CryptographicException(SR.Cryptography_Der_Invalid_Encoding);
        }

        /// <summary>
        /// Get a ReadOnlyMemory view of the next encoded value without consuming it.
        /// For indefinite length encodings this includes the End of Contents marker.
        /// </summary>
        /// <returns>A ReadOnlyMemory view of the next encoded value.</returns>
        /// <exception cref="CryptographicException">
        /// The reader is positioned at a point where the tag or length is invalid
        /// under the current encoding rules.
        /// </exception>
        /// <seealso cref="PeekContentBytes"/>
        /// <seealso cref="GetEncodedValue"/>
        public ReadOnlyMemory<byte> PeekEncodedValue() {
            var tag = ReadTagAndLength(out var length, out var bytesRead);

            if (length == null) {
                var contentsLength = SeekEndOfContents(_data.Slice(bytesRead));
                return Slice(_data, 0, bytesRead + contentsLength + kEndOfContentsEncodedLength);
            }

            return Slice(_data, 0, bytesRead + length.Value);
        }

        /// <summary>
        /// Get a ReadOnlyMemory view of the content octets (bytes) of the next encoded
        /// value without consuming it.
        /// </summary>
        /// <returns>A ReadOnlyMemory view of the contents octets of the next encoded value.</returns>
        /// <exception cref="CryptographicException">
        /// The reader is positioned at a point where the tag or length is invalid
        /// under the current encoding rules.
        /// </exception>
        /// <seealso cref="PeekEncodedValue"/>
        public ReadOnlyMemory<byte> PeekContentBytes() {
            var tag = ReadTagAndLength(out var length, out var bytesRead);

            if (length == null) {
                return Slice(_data, bytesRead, SeekEndOfContents(_data.Slice(bytesRead)));
            }

            return Slice(_data, bytesRead, length.Value);
        }

        /// <summary>
        /// Get a ReadOnlyMemory view of the next encoded value, and move the reader past it.
        /// For an indefinite length encoding this includes the End of Contents marker.
        /// </summary>
        /// <returns>A ReadOnlyMemory view of the next encoded value.</returns>
        /// <seealso cref="PeekEncodedValue"/>
        public ReadOnlyMemory<byte> GetEncodedValue() {
            var encodedValue = PeekEncodedValue();
            _data = _data.Slice(encodedValue.Length);
            return encodedValue;
        }

        private static bool ReadBooleanValue(
            ReadOnlySpan<byte> source,
            AsnEncodingRules ruleSet) {
            // T-REC-X.690-201508 sec 8.2.1
            if (source.Length != 1) {
                throw new CryptographicException(SR.Cryptography_Der_Invalid_Encoding);
            }

            var val = source[0];

            // T-REC-X.690-201508 sec 8.2.2
            if (val == 0) {
                return false;
            }

            // T-REC-X.690-201508 sec 11.1
            if (val != 0xFF && (ruleSet == AsnEncodingRules.DER || ruleSet == AsnEncodingRules.CER)) {
                throw new CryptographicException(SR.Cryptography_Der_Invalid_Encoding);
            }

            return true;
        }

        public bool ReadBoolean() {
            return ReadBoolean(Asn1Tag.Boolean);
        }

        public bool ReadBoolean(Asn1Tag expectedTag) {
            var tag = ReadTagAndLength(out var length, out var headerLength);
            CheckExpectedTag(tag, expectedTag, UniversalTagNumber.Boolean);

            // T-REC-X.690-201508 sec 8.2.1
            if (tag.IsConstructed) {
                throw new CryptographicException(SR.Cryptography_Der_Invalid_Encoding);
            }

            var value = ReadBooleanValue(
                Slice(_data, headerLength, length.Value).Span,
                _ruleSet);

            _data = _data.Slice(headerLength + length.Value);
            return value;
        }

        private ReadOnlyMemory<byte> GetIntegerContents(
            Asn1Tag expectedTag,
            UniversalTagNumber tagNumber,
            out int headerLength) {
            var tag = ReadTagAndLength(out var length, out headerLength);
            CheckExpectedTag(tag, expectedTag, tagNumber);

            // T-REC-X.690-201508 sec 8.3.1
            if (tag.IsConstructed || length < 1) {
                throw new CryptographicException(SR.Cryptography_Der_Invalid_Encoding);
            }

            // Slice first so that an out of bounds value triggers a CryptographicException.
            var contents = Slice(_data, headerLength, length.Value);
            var contentSpan = contents.Span;

            // T-REC-X.690-201508 sec 8.3.2
            if (contents.Length > 1) {
                var bigEndianValue = (ushort)((contentSpan[0] << 8) | contentSpan[1]);
                const ushort RedundancyMask = 0b1111_1111_1000_0000;
                var masked = (ushort)(bigEndianValue & RedundancyMask);

                // If the first 9 bits are all 0 or are all 1, the value is invalid.
                if (masked == 0 || masked == RedundancyMask) {
                    throw new CryptographicException(SR.Cryptography_Der_Invalid_Encoding);
                }
            }

            return contents;
        }

        public ReadOnlyMemory<byte> GetIntegerBytes() {
            return GetIntegerBytes(Asn1Tag.Integer);
        }

        public ReadOnlyMemory<byte> GetIntegerBytes(Asn1Tag expectedTag) {
            var contents =
                GetIntegerContents(expectedTag, UniversalTagNumber.Integer, out var headerLength);

            _data = _data.Slice(headerLength + contents.Length);
            return contents;
        }

        public BigInteger GetInteger() {
            return GetInteger(Asn1Tag.Integer);
        }

        public BigInteger GetInteger(Asn1Tag expectedTag) {
            var contents =
                GetIntegerContents(expectedTag, UniversalTagNumber.Integer, out var headerLength);

            // TODO: Split this for netcoreapp/netstandard to use the Big-Endian BigInteger parsing
            var tmp = ArrayPool<byte>.Shared.Rent(contents.Length);
            BigInteger value;

            try {
                var fill = (contents.Span[0] & 0x80) == 0 ? (byte)0 : (byte)0xFF;
                // Fill the unused portions of tmp with positive or negative padding.
                new Span<byte>(tmp, contents.Length, tmp.Length - contents.Length).Fill(fill);
                contents.CopyTo(tmp);
                // Convert to Little-Endian.
                AsnWriter.Reverse(new Span<byte>(tmp, 0, contents.Length));
                value = new BigInteger(tmp);
            }
            finally {
                // Clear the whole tmp so that not even the sign bit is returned to the array pool.
                Array.Clear(tmp, 0, tmp.Length);
                ArrayPool<byte>.Shared.Return(tmp);
            }

            _data = _data.Slice(headerLength + contents.Length);
            return value;
        }

        private bool TryReadSignedInteger(
            int sizeLimit,
            Asn1Tag expectedTag,
            UniversalTagNumber tagNumber,
            out long value) {
            Debug.Assert(sizeLimit <= sizeof(long));

            var contents = GetIntegerContents(expectedTag, tagNumber, out var headerLength);

            if (contents.Length > sizeLimit) {
                value = 0;
                return false;
            }

            var contentSpan = contents.Span;

            var isNegative = (contentSpan[0] & 0x80) != 0;
            long accum = isNegative ? -1 : 0;

            for (var i = 0; i < contents.Length; i++) {
                accum <<= 8;
                accum |= contentSpan[i];
            }

            _data = _data.Slice(headerLength + contents.Length);
            value = accum;
            return true;
        }

        private bool TryReadUnsignedInteger(
            int sizeLimit,
            Asn1Tag expectedTag,
            UniversalTagNumber tagNumber,
            out ulong value) {
            Debug.Assert(sizeLimit <= sizeof(ulong));

            var contents = GetIntegerContents(expectedTag, tagNumber, out var headerLength);
            var contentSpan = contents.Span;
            var contentLength = contents.Length;

            var isNegative = (contentSpan[0] & 0x80) != 0;

            if (isNegative) {
                value = 0;
                return false;
            }

            // Ignore any padding zeros.
            if (contentSpan.Length > 1 && contentSpan[0] == 0) {
                contentSpan = contentSpan.Slice(1);
            }

            if (contentSpan.Length > sizeLimit) {
                value = 0;
                return false;
            }

            ulong accum = 0;

            for (var i = 0; i < contentSpan.Length; i++) {
                accum <<= 8;
                accum |= contentSpan[i];
            }

            _data = _data.Slice(headerLength + contentLength);
            value = accum;
            return true;
        }

        public bool TryReadInt32(out int value) {
            return TryReadInt32(Asn1Tag.Integer, out value);
        }

        public bool TryReadInt32(Asn1Tag expectedTag, out int value) {
            if (TryReadSignedInteger(sizeof(int), expectedTag, UniversalTagNumber.Integer, out var longValue)) {
                value = (int)longValue;
                return true;
            }

            value = 0;
            return false;
        }

        public bool TryReadUInt32(out uint value) {
            return TryReadUInt32(Asn1Tag.Integer, out value);
        }

        public bool TryReadUInt32(Asn1Tag expectedTag, out uint value) {
            if (TryReadUnsignedInteger(sizeof(uint), expectedTag, UniversalTagNumber.Integer, out var ulongValue)) {
                value = (uint)ulongValue;
                return true;
            }

            value = 0;
            return false;
        }

        public bool TryReadInt64(out long value) {
            return TryReadInt64(Asn1Tag.Integer, out value);
        }

        public bool TryReadInt64(Asn1Tag expectedTag, out long value) {
            return TryReadSignedInteger(sizeof(long), expectedTag, UniversalTagNumber.Integer, out value);
        }

        public bool TryReadUInt64(out ulong value) {
            return TryReadUInt64(Asn1Tag.Integer, out value);
        }

        public bool TryReadUInt64(Asn1Tag expectedTag, out ulong value) {
            return TryReadUnsignedInteger(sizeof(ulong), expectedTag, UniversalTagNumber.Integer, out value);
        }

        public bool TryReadInt16(out short value) {
            return TryReadInt16(Asn1Tag.Integer, out value);
        }

        public bool TryReadInt16(Asn1Tag expectedTag, out short value) {
            if (TryReadSignedInteger(sizeof(short), expectedTag, UniversalTagNumber.Integer, out var longValue)) {
                value = (short)longValue;
                return true;
            }

            value = 0;
            return false;
        }

        public bool TryReadUInt16(out ushort value) {
            return TryReadUInt16(Asn1Tag.Integer, out value);
        }

        public bool TryReadUInt16(Asn1Tag expectedTag, out ushort value) {
            if (TryReadUnsignedInteger(sizeof(ushort), expectedTag, UniversalTagNumber.Integer, out var ulongValue)) {
                value = (ushort)ulongValue;
                return true;
            }

            value = 0;
            return false;
        }

        public bool TryReadInt8(out sbyte value) {
            return TryReadInt8(Asn1Tag.Integer, out value);
        }

        public bool TryReadInt8(Asn1Tag expectedTag, out sbyte value) {
            if (TryReadSignedInteger(sizeof(sbyte), expectedTag, UniversalTagNumber.Integer, out var longValue)) {
                value = (sbyte)longValue;
                return true;
            }

            value = 0;
            return false;
        }

        public bool TryReadUInt8(out byte value) {
            return TryReadUInt8(Asn1Tag.Integer, out value);
        }

        public bool TryReadUInt8(Asn1Tag expectedTag, out byte value) {
            if (TryReadUnsignedInteger(sizeof(byte), expectedTag, UniversalTagNumber.Integer, out var ulongValue)) {
                value = (byte)ulongValue;
                return true;
            }

            value = 0;
            return false;
        }

        private void ParsePrimitiveBitStringContents(
            ReadOnlyMemory<byte> source,
            out int unusedBitCount,
            out ReadOnlyMemory<byte> value,
            out byte normalizedLastByte) {
            // T-REC-X.690-201508 sec 9.2
            if (_ruleSet == AsnEncodingRules.CER && source.Length > MaxCERSegmentSize) {
                throw new CryptographicException(SR.Cryptography_Der_Invalid_Encoding);
            }

            // T-REC-X.690-201508 sec 8.6.2.3
            if (source.Length == 0) {
                throw new CryptographicException(SR.Cryptography_Der_Invalid_Encoding);
            }

            var sourceSpan = source.Span;
            unusedBitCount = sourceSpan[0];

            // T-REC-X.690-201508 sec 8.6.2.2
            if (unusedBitCount > 7) {
                throw new CryptographicException(SR.Cryptography_Der_Invalid_Encoding);
            }

            if (source.Length == 1) {
                // T-REC-X.690-201508 sec 8.6.2.4
                if (unusedBitCount > 0) {
                    throw new CryptographicException(SR.Cryptography_Der_Invalid_Encoding);
                }

                Debug.Assert(unusedBitCount == 0);
                value = ReadOnlyMemory<byte>.Empty;
                normalizedLastByte = 0;
                return;
            }

            // Build a mask for the bits that are used so the normalized value can be computed
            //
            // If 3 bits are "unused" then build a mask for them to check for 0.
            // -1 << 3 => 0b1111_1111 << 3 => 0b1111_1000
            var mask = -1 << unusedBitCount;
            var lastByte = sourceSpan[sourceSpan.Length - 1];
            var maskedByte = (byte)(lastByte & mask);

            if (maskedByte != lastByte) {
                // T-REC-X.690-201508 sec 11.2.1
                if (_ruleSet == AsnEncodingRules.DER || _ruleSet == AsnEncodingRules.CER) {
                    throw new CryptographicException(SR.Cryptography_Der_Invalid_Encoding);
                }
            }

            normalizedLastByte = maskedByte;
            value = source.Slice(1);
        }

        private delegate void BitStringCopyAction(
            ReadOnlyMemory<byte> value,
            byte normalizedLastByte,
            Span<byte> destination);

        private static void CopyBitStringValue(
            ReadOnlyMemory<byte> value,
            byte normalizedLastByte,
            Span<byte> destination) {
            if (value.Length == 0) {
                return;
            }

            value.Span.CopyTo(destination);
            // Replace the last byte with the normalized answer.
            destination[value.Length - 1] = normalizedLastByte;
        }

        private int CountConstructedBitString(ReadOnlyMemory<byte> source, bool isIndefinite) {
            var destination = Span<byte>.Empty;

            return ProcessConstructedBitString(
                source,
                destination,
                null,
                isIndefinite,
                out _,
                out _);
        }

        private void CopyConstructedBitString(
            ReadOnlyMemory<byte> source,
            Span<byte> destination,
            bool isIndefinite,
            out int unusedBitCount,
            out int bytesRead,
            out int bytesWritten) {
            var tmpDest = destination;

            bytesWritten = ProcessConstructedBitString(
                source,
                tmpDest,
                (value, lastByte, dest) => CopyBitStringValue(value, lastByte, dest),
                isIndefinite,
                out unusedBitCount,
                out bytesRead);
        }

        private int ProcessConstructedBitString(
            ReadOnlyMemory<byte> source,
            Span<byte> destination,
            BitStringCopyAction copyAction,
            bool isIndefinite,
            out int lastUnusedBitCount,
            out int bytesRead) {
            lastUnusedBitCount = 0;
            bytesRead = 0;
            var lastSegmentLength = MaxCERSegmentSize;

            var tmpReader = new AsnReader(source, _ruleSet);
            Stack<(AsnReader, bool, int)> readerStack = null;
            var totalLength = 0;
            var tag = Asn1Tag.ConstructedBitString;
            var curDest = destination;

            do {
                while (tmpReader.HasData) {
                    tag = tmpReader.ReadTagAndLength(out var length, out var headerLength);

                    if (tag == Asn1Tag.PrimitiveBitString) {
                        if (lastUnusedBitCount != 0) {
                            // T-REC-X.690-201508 sec 8.6.4, only the last segment may have
                            // a number of bits not a multiple of 8.
                            throw new CryptographicException(SR.Cryptography_Der_Invalid_Encoding);
                        }

                        if (_ruleSet == AsnEncodingRules.CER && lastSegmentLength != MaxCERSegmentSize) {
                            // T-REC-X.690-201508 sec 9.2
                            throw new CryptographicException(SR.Cryptography_Der_Invalid_Encoding);
                        }

                        Debug.Assert(length != null);
                        var encodedValue = Slice(tmpReader._data, headerLength, length.Value);

                        ParsePrimitiveBitStringContents(
                            encodedValue,
                            out lastUnusedBitCount,
                            out var contents,
                            out var normalizedLastByte);

                        var localLen = headerLength + encodedValue.Length;
                        tmpReader._data = tmpReader._data.Slice(localLen);

                        bytesRead += localLen;
                        totalLength += contents.Length;
                        lastSegmentLength = encodedValue.Length;

                        if (copyAction != null) {
                            copyAction(contents, normalizedLastByte, curDest);
                            curDest = curDest.Slice(contents.Length);
                        }
                    }
                    else if (tag == Asn1Tag.EndOfContents && isIndefinite) {
                        ValidateEndOfContents(tag, length, headerLength);

                        bytesRead += headerLength;

                        if (readerStack?.Count > 0) {
                            (var topReader, var wasIndefinite, var pushedBytesRead) = readerStack.Pop();
                            topReader._data = topReader._data.Slice(bytesRead);

                            bytesRead += pushedBytesRead;
                            isIndefinite = wasIndefinite;
                            tmpReader = topReader;
                        }
                        else {
                            // We have matched the EndOfContents that brought us here.
                            break;
                        }
                    }
                    else if (tag == Asn1Tag.ConstructedBitString) {
                        if (_ruleSet == AsnEncodingRules.CER) {
                            // T-REC-X.690-201508 sec 9.2
                            throw new CryptographicException(SR.Cryptography_Der_Invalid_Encoding);
                        }

                        if (readerStack == null) {
                            readerStack = new Stack<(AsnReader, bool, int)>();
                        }

                        readerStack.Push((tmpReader, isIndefinite, bytesRead));

                        tmpReader = new AsnReader(
                            Slice(tmpReader._data, headerLength, length),
                            _ruleSet);

                        bytesRead = headerLength;
                        isIndefinite = length == null;
                    }
                    else {
                        // T-REC-X.690-201508 sec 8.6.4.1 (in particular, Note 2)
                        throw new CryptographicException(SR.Cryptography_Der_Invalid_Encoding);
                    }
                }

                if (isIndefinite && tag != Asn1Tag.EndOfContents) {
                    throw new CryptographicException(SR.Cryptography_Der_Invalid_Encoding);
                }

                if (readerStack?.Count > 0) {
                    (var topReader, var wasIndefinite, var pushedBytesRead) = readerStack.Pop();

                    tmpReader = topReader;
                    tmpReader._data = tmpReader._data.Slice(bytesRead);

                    isIndefinite = wasIndefinite;
                    bytesRead += pushedBytesRead;
                }
                else {
                    tmpReader = null;
                }
            } while (tmpReader != null);

            return totalLength;
        }

        private bool TryCopyConstructedBitStringValue(
            ReadOnlyMemory<byte> source,
            Span<byte> dest,
            bool isIndefinite,
            out int unusedBitCount,
            out int bytesRead,
            out int bytesWritten) {
            // Call CountConstructedBitString to get the required byte and to verify that the
            // data is well-formed before copying into dest.
            var contentLength = CountConstructedBitString(source, isIndefinite);

            // Since the unused bits byte from the segments don't count, only one segment
            // returns 999 (or less), the second segment bumps the count to 1000, and is legal.
            //
            // T-REC-X.690-201508 sec 9.2
            if (_ruleSet == AsnEncodingRules.CER && contentLength < MaxCERSegmentSize) {
                throw new CryptographicException(SR.Cryptography_Der_Invalid_Encoding);
            }

            if (dest.Length < contentLength) {
                unusedBitCount = 0;
                bytesRead = 0;
                bytesWritten = 0;
                return false;
            }

            CopyConstructedBitString(
                source,
                dest,
                isIndefinite,
                out unusedBitCount,
                out bytesRead,
                out bytesWritten);

            Debug.Assert(bytesWritten == contentLength);
            return true;
        }

        private bool TryGetPrimitiveBitStringValue(
            Asn1Tag expectedTag,
            out Asn1Tag actualTag,
            out int? contentsLength,
            out int headerLength,
            out int unusedBitCount,
            out ReadOnlyMemory<byte> value,
            out byte normalizedLastByte) {
            actualTag = ReadTagAndLength(out contentsLength, out headerLength);
            CheckExpectedTag(actualTag, expectedTag, UniversalTagNumber.BitString);

            if (actualTag.IsConstructed) {
                if (_ruleSet == AsnEncodingRules.DER) {
                    throw new CryptographicException(SR.Cryptography_Der_Invalid_Encoding);
                }

                unusedBitCount = 0;
                value = default;
                normalizedLastByte = 0;
                return false;
            }

            Debug.Assert(contentsLength.HasValue);
            var encodedValue = Slice(_data, headerLength, contentsLength.Value);

            ParsePrimitiveBitStringContents(
                encodedValue,
                out unusedBitCount,
                out value,
                out normalizedLastByte);

            return true;
        }

        public bool TryGetPrimitiveBitStringValue(out int unusedBitCount, out ReadOnlyMemory<byte> contents) {
            return TryGetPrimitiveBitStringValue(Asn1Tag.PrimitiveBitString, out unusedBitCount, out contents);
        }

        /// <summary>
        /// Gets a ReadOnlyMemory view over the data value portion of the contents of a bit string.
        /// </summary>
        /// <param name="expectedTag">The expected tag to read</param>
        /// <param name="unusedBitCount">The encoded value for the number of unused bits.</param>
        /// <param name="value">The data value portion of the bit string contents.</param>
        /// <returns>
        ///   <c>true</c> if the bit string uses a primitive encoding and the "unused" bits have value 0,
        ///   <c>false</c> otherwise.
        /// </returns>
        /// <exception cref="CryptographicException">
        ///  <ul>
        ///   <li>No data remains</li>
        ///   <li>The tag read does not match the expected tag</li>
        ///   <li>The length is invalid under the chosen encoding rules</li>
        ///   <li>The unusedBitCount value is out of bounds</li>
        ///   <li>A CER or DER encoding was chosen and an "unused" bit was set to 1</li>
        ///   <li>A CER encoding was chosen and the primitive content length exceeds the maximum allowed</li>
        /// </ul>
        /// </exception>
        public bool TryGetPrimitiveBitStringValue(
            Asn1Tag expectedTag,
            out int unusedBitCount,
            out ReadOnlyMemory<byte> value) {
            var isPrimitive = TryGetPrimitiveBitStringValue(
                expectedTag,
                out var actualTag,
                out var contentsLength,
                out var headerLength,
                out unusedBitCount,
                out value,
                out var normalizedLastByte);

            if (isPrimitive) {
                // A BER reader which encountered a situation where an "unused" bit was not
                // set to 0.
                if (value.Length != 0 && normalizedLastByte != value.Span[value.Length - 1]) {
                    unusedBitCount = 0;
                    value = default;
                    return false;
                }

                // Skip the tag+length (header) and the unused bit count byte (1) and the contents.
                _data = _data.Slice(headerLength + value.Length + 1);
            }

            return isPrimitive;
        }

        public bool TryCopyBitStringBytes(
            Span<byte> destination,
            out int unusedBitCount,
            out int bytesWritten) {
            return TryCopyBitStringBytes(
                Asn1Tag.PrimitiveBitString,
                destination,
                out unusedBitCount,
                out bytesWritten);
        }

        public bool TryCopyBitStringBytes(
            Asn1Tag expectedTag,
            Span<byte> destination,
            out int unusedBitCount,
            out int bytesWritten) {
            if (TryGetPrimitiveBitStringValue(
                expectedTag,
                out var actualTag,
                out var contentsLength,
                out var headerLength,
                out unusedBitCount,
                out var value,
                out var normalizedLastByte)) {
                if (value.Length > destination.Length) {
                    bytesWritten = 0;
                    unusedBitCount = 0;
                    return false;
                }

                CopyBitStringValue(value, normalizedLastByte, destination);

                bytesWritten = value.Length;
                // contents doesn't include the unusedBitCount value, so add one byte for that.
                _data = _data.Slice(headerLength + value.Length + 1);
                return true;
            }

            Debug.Assert(actualTag.IsConstructed);

            var read = TryCopyConstructedBitStringValue(
                Slice(_data, headerLength, contentsLength),
                destination,
                contentsLength == null,
                out unusedBitCount,
                out var bytesRead,
                out bytesWritten);

            if (read) {
                _data = _data.Slice(headerLength + bytesRead);
            }

            return read;
        }

        public TFlagsEnum GetNamedBitListValue<TFlagsEnum>() where TFlagsEnum : struct {
            return GetNamedBitListValue<TFlagsEnum>(Asn1Tag.PrimitiveBitString);
        }

        public TFlagsEnum GetNamedBitListValue<TFlagsEnum>(Asn1Tag expectedTag) where TFlagsEnum : struct {
            var tFlagsEnum = typeof(TFlagsEnum);

            return (TFlagsEnum)Enum.ToObject(tFlagsEnum, GetNamedBitListValue(expectedTag, tFlagsEnum));
        }

        public Enum GetNamedBitListValue(Type tFlagsEnum) {
            return GetNamedBitListValue(Asn1Tag.PrimitiveBitString, tFlagsEnum);
        }

        public Enum GetNamedBitListValue(Asn1Tag expectedTag, Type tFlagsEnum) {
            // This will throw an ArgumentException if TEnum isn't an enum type,
            // so we don't need to validate it.
            var backingType = tFlagsEnum.GetEnumUnderlyingType();

            if (!tFlagsEnum.IsDefined(typeof(FlagsAttribute), false)) {
                throw new ArgumentException(
                    SR.Cryptography_Asn_NamedBitListRequiresFlagsEnum,
                    nameof(tFlagsEnum));
            }

            var sizeLimit = Marshal.SizeOf(backingType);
            Span<byte> stackSpan = stackalloc byte[sizeLimit];
            var saveData = _data;

            // If TryCopyBitStringBytes succeeds but anything else fails _data will have moved,
            // so if anything throws here just move _data back to what it was.
            try {
                if (!TryCopyBitStringBytes(expectedTag, stackSpan, out var unusedBitCount, out var bytesWritten)) {
                    throw new CryptographicException(
                        string.Format(SR.Cryptography_Asn_NamedBitListValueTooBig, tFlagsEnum.Name));
                }

                if (bytesWritten == 0) {
                    // The mode isn't relevant, zero is always zero.
                    return (Enum)Enum.ToObject(tFlagsEnum, 0);
                }

                ReadOnlySpan<byte> valueSpan = stackSpan.Slice(0, bytesWritten);

                // Now that the 0-bounds check is out of the way:
                //
                // T-REC-X.690-201508 sec 11.2.2
                if (_ruleSet == AsnEncodingRules.DER ||
                    _ruleSet == AsnEncodingRules.CER) {
                    var lastByte = valueSpan[bytesWritten - 1];

                    // No unused bits tests 0x01, 1 is 0x02, 2 is 0x04, etc.
                    // We already know that TryCopyBitStringBytes checked that the
                    // declared unused bits were 0, this checks that the last "used" bit
                    // isn't also zero.
                    var testBit = (byte)(1 << unusedBitCount);

                    if ((lastByte & testBit) == 0) {
                        throw new CryptographicException(SR.Cryptography_Der_Invalid_Encoding);
                    }
                }

                // Consider a NamedBitList defined as
                //
                //   SomeList ::= BIT STRING {
                //     a(0), b(1), c(2), d(3), e(4), f(5), g(6), h(7), i(8), j(9), k(10)
                //   }
                //
                // The BIT STRING encoding of (a | j) is
                //   unusedBitCount = 6,
                //   contents: 0x80 0x40  (0b10000000_01000000)
                //
                // A the C# exposure of this structure we adhere to is
                //
                // [Flags]
                // enum SomeList
                // {
                //     A = 1,
                //     B = 1 << 1,
                //     C = 1 << 2,
                //     ...
                // }
                //
                // Which happens to be exactly backwards from how the bits are encoded, but the complexity
                // only needs to live here.
                return (Enum)Enum.ToObject(tFlagsEnum, InterpretNamedBitListReversed(valueSpan));
            }
            catch {
                _data = saveData;
                throw;
            }
        }

        private static long InterpretNamedBitListReversed(ReadOnlySpan<byte> valueSpan) {
            Debug.Assert(valueSpan.Length <= sizeof(long));

            long accum = 0;
            long currentBitValue = 1;

            for (var byteIdx = 0; byteIdx < valueSpan.Length; byteIdx++) {
                var byteVal = valueSpan[byteIdx];

                for (var bitIndex = 7; bitIndex >= 0; bitIndex--) {
                    var test = 1 << bitIndex;

                    if ((byteVal & test) != 0) {
                        accum |= currentBitValue;
                    }

                    currentBitValue <<= 1;
                }
            }

            return accum;
        }

        public ReadOnlyMemory<byte> GetEnumeratedBytes() {
            return GetEnumeratedBytes(Asn1Tag.Enumerated);
        }

        public ReadOnlyMemory<byte> GetEnumeratedBytes(Asn1Tag expectedTag) {
            // T-REC-X.690-201508 sec 8.4 says the contents are the same as for integers.
            var contents =
                GetIntegerContents(expectedTag, UniversalTagNumber.Enumerated, out var headerLength);

            _data = _data.Slice(headerLength + contents.Length);
            return contents;
        }

        public TEnum GetEnumeratedValue<TEnum>() where TEnum : struct {
            var tEnum = typeof(TEnum);

            return (TEnum)Enum.ToObject(tEnum, GetEnumeratedValue(tEnum));
        }

        public TEnum GetEnumeratedValue<TEnum>(Asn1Tag expectedTag) where TEnum : struct {
            var tEnum = typeof(TEnum);

            return (TEnum)Enum.ToObject(tEnum, GetEnumeratedValue(expectedTag, tEnum));
        }

        public Enum GetEnumeratedValue(Type tEnum) {
            return GetEnumeratedValue(Asn1Tag.Enumerated, tEnum);
        }

        public Enum GetEnumeratedValue(Asn1Tag expectedTag, Type tEnum) {
            const UniversalTagNumber tagNumber = UniversalTagNumber.Enumerated;

            // This will throw an ArgumentException if TEnum isn't an enum type,
            // so we don't need to validate it.
            var backingType = tEnum.GetEnumUnderlyingType();

            if (tEnum.IsDefined(typeof(FlagsAttribute), false)) {
                throw new ArgumentException(
                    SR.Cryptography_Asn_EnumeratedValueRequiresNonFlagsEnum,
                    nameof(tEnum));
            }

            // T-REC-X.690-201508 sec 8.4 says the contents are the same as for integers.
            var sizeLimit = Marshal.SizeOf(backingType);

            if (backingType == typeof(int) ||
                backingType == typeof(long) ||
                backingType == typeof(short) ||
                backingType == typeof(sbyte)) {
                if (!TryReadSignedInteger(sizeLimit, expectedTag, tagNumber, out var value)) {
                    throw new CryptographicException(SR.Cryptography_Der_Invalid_Encoding);
                }

                return (Enum)Enum.ToObject(tEnum, value);
            }

            if (backingType == typeof(uint) ||
                backingType == typeof(ulong) ||
                backingType == typeof(ushort) ||
                backingType == typeof(byte)) {
                if (!TryReadUnsignedInteger(sizeLimit, expectedTag, tagNumber, out var value)) {
                    throw new CryptographicException(SR.Cryptography_Der_Invalid_Encoding);
                }

                return (Enum)Enum.ToObject(tEnum, value);
            }

            Debug.Fail($"No handler for type {backingType.Name}");
            throw new CryptographicException();
        }

        private bool TryGetPrimitiveOctetStringBytes(
            Asn1Tag expectedTag,
            out Asn1Tag actualTag,
            out int? contentLength,
            out int headerLength,
            out ReadOnlyMemory<byte> contents,
            UniversalTagNumber universalTagNumber = UniversalTagNumber.OctetString) {
            actualTag = ReadTagAndLength(out contentLength, out headerLength);
            CheckExpectedTag(actualTag, expectedTag, universalTagNumber);

            if (actualTag.IsConstructed) {
                if (_ruleSet == AsnEncodingRules.DER) {
                    throw new CryptographicException(SR.Cryptography_Der_Invalid_Encoding);
                }

                contents = default;
                return false;
            }

            Debug.Assert(contentLength.HasValue);
            var encodedValue = Slice(_data, headerLength, contentLength.Value);

            if (_ruleSet == AsnEncodingRules.CER && encodedValue.Length > MaxCERSegmentSize) {
                throw new CryptographicException(SR.Cryptography_Der_Invalid_Encoding);
            }

            contents = encodedValue;
            return true;
        }

        private bool TryGetPrimitiveOctetStringBytes(
            Asn1Tag expectedTag,
            UniversalTagNumber universalTagNumber,
            out ReadOnlyMemory<byte> contents) {
            if (TryGetPrimitiveOctetStringBytes(expectedTag, out _, out _, out var headerLength, out contents, universalTagNumber)) {
                _data = _data.Slice(headerLength + contents.Length);
                return true;
            }

            return false;
        }

        public bool TryGetPrimitiveOctetStringBytes(out ReadOnlyMemory<byte> contents) {
            return TryGetPrimitiveOctetStringBytes(Asn1Tag.PrimitiveOctetString, out contents);
        }

        /// <summary>
        /// Gets the contents for an octet string under a primitive encoding.
        /// </summary>
        /// <param name="expectedTag">The expected tag value</param>
        /// <param name="contents">The contents for the octet string.</param>
        /// <returns>
        ///   <c>true</c> if the octet string uses a primitive encoding, <c>false</c> otherwise.
        /// </returns>
        /// <exception cref="CryptographicException">
        ///  <ul>
        ///   <li>No data remains</li>
        ///   <li>The tag read did not match the expected tag</li>
        ///   <li>The length is invalid under the chosen encoding rules</li>
        ///   <li>A CER encoding was chosen and the primitive content length exceeds the maximum allowed</li>
        /// </ul>
        /// </exception>
        public bool TryGetPrimitiveOctetStringBytes(Asn1Tag expectedTag, out ReadOnlyMemory<byte> contents) {
            return TryGetPrimitiveOctetStringBytes(expectedTag, UniversalTagNumber.OctetString, out contents);
        }

        private int CountConstructedOctetString(ReadOnlyMemory<byte> source, bool isIndefinite) {
            var contentLength = CopyConstructedOctetString(
                source,
                Span<byte>.Empty,
                false,
                isIndefinite,
                out _);

            // T-REC-X.690-201508 sec 9.2
            if (_ruleSet == AsnEncodingRules.CER && contentLength <= MaxCERSegmentSize) {
                throw new CryptographicException(SR.Cryptography_Der_Invalid_Encoding);
            }

            return contentLength;
        }

        private void CopyConstructedOctetString(
            ReadOnlyMemory<byte> source,
            Span<byte> destination,
            bool isIndefinite,
            out int bytesRead,
            out int bytesWritten) {
            bytesWritten = CopyConstructedOctetString(
                source,
                destination,
                true,
                isIndefinite,
                out bytesRead);
        }

        private int CopyConstructedOctetString(
            ReadOnlyMemory<byte> source,
            Span<byte> destination,
            bool write,
            bool isIndefinite,
            out int bytesRead) {
            bytesRead = 0;
            var lastSegmentLength = MaxCERSegmentSize;

            var tmpReader = new AsnReader(source, _ruleSet);
            Stack<(AsnReader, bool, int)> readerStack = null;
            var totalLength = 0;
            var tag = Asn1Tag.ConstructedBitString;
            var curDest = destination;

            do {
                while (tmpReader.HasData) {
                    tag = tmpReader.ReadTagAndLength(out var length, out var headerLength);

                    if (tag == Asn1Tag.PrimitiveOctetString) {
                        if (_ruleSet == AsnEncodingRules.CER && lastSegmentLength != MaxCERSegmentSize) {
                            // T-REC-X.690-201508 sec 9.2
                            throw new CryptographicException(SR.Cryptography_Der_Invalid_Encoding);
                        }

                        Debug.Assert(length != null);

                        // The call to Slice here sanity checks the data bounds, length.Value is not
                        // reliable unless this call has succeeded.
                        var contents = Slice(tmpReader._data, headerLength, length.Value);

                        var localLen = headerLength + contents.Length;
                        tmpReader._data = tmpReader._data.Slice(localLen);

                        bytesRead += localLen;
                        totalLength += contents.Length;
                        lastSegmentLength = contents.Length;

                        if (_ruleSet == AsnEncodingRules.CER && lastSegmentLength > MaxCERSegmentSize) {
                            // T-REC-X.690-201508 sec 9.2
                            throw new CryptographicException(SR.Cryptography_Der_Invalid_Encoding);
                        }

                        if (write) {
                            contents.Span.CopyTo(curDest);
                            curDest = curDest.Slice(contents.Length);
                        }
                    }
                    else if (tag == Asn1Tag.EndOfContents && isIndefinite) {
                        ValidateEndOfContents(tag, length, headerLength);

                        bytesRead += headerLength;

                        if (readerStack?.Count > 0) {
                            (var topReader, var wasIndefinite, var pushedBytesRead) = readerStack.Pop();
                            topReader._data = topReader._data.Slice(bytesRead);

                            bytesRead += pushedBytesRead;
                            isIndefinite = wasIndefinite;
                            tmpReader = topReader;
                        }
                        else {
                            // We have matched the EndOfContents that brought us here.
                            break;
                        }
                    }
                    else if (tag == Asn1Tag.ConstructedOctetString) {
                        if (_ruleSet == AsnEncodingRules.CER) {
                            // T-REC-X.690-201508 sec 9.2
                            throw new CryptographicException(SR.Cryptography_Der_Invalid_Encoding);
                        }

                        if (readerStack == null) {
                            readerStack = new Stack<(AsnReader, bool, int)>();
                        }

                        readerStack.Push((tmpReader, isIndefinite, bytesRead));

                        tmpReader = new AsnReader(
                            Slice(tmpReader._data, headerLength, length),
                            _ruleSet);

                        bytesRead = headerLength;
                        isIndefinite = length == null;
                    }
                    else {
                        // T-REC-X.690-201508 sec 8.6.4.1 (in particular, Note 2)
                        throw new CryptographicException(SR.Cryptography_Der_Invalid_Encoding);
                    }
                }

                if (isIndefinite && tag != Asn1Tag.EndOfContents) {
                    throw new CryptographicException(SR.Cryptography_Der_Invalid_Encoding);
                }

                if (readerStack?.Count > 0) {
                    (var topReader, var wasIndefinite, var pushedBytesRead) = readerStack.Pop();

                    tmpReader = topReader;
                    tmpReader._data = tmpReader._data.Slice(bytesRead);

                    isIndefinite = wasIndefinite;
                    bytesRead += pushedBytesRead;
                }
                else {
                    tmpReader = null;
                }
            } while (tmpReader != null);

            return totalLength;
        }

        private bool TryCopyConstructedOctetStringContents(
            ReadOnlyMemory<byte> source,
            Span<byte> dest,
            bool isIndefinite,
            out int bytesRead,
            out int bytesWritten) {
            bytesRead = 0;

            var contentLength = CountConstructedOctetString(source, isIndefinite);

            if (dest.Length < contentLength) {
                bytesWritten = 0;
                return false;
            }

            CopyConstructedOctetString(source, dest, isIndefinite, out bytesRead, out bytesWritten);

            Debug.Assert(bytesWritten == contentLength);
            return true;
        }

        public bool TryCopyOctetStringBytes(
            Span<byte> destination,
            out int bytesWritten) {
            return TryCopyOctetStringBytes(
                Asn1Tag.PrimitiveOctetString,
                destination,
                out bytesWritten);
        }

        public bool TryCopyOctetStringBytes(
            Asn1Tag expectedTag,
            Span<byte> destination,
            out int bytesWritten) {
            if (TryGetPrimitiveOctetStringBytes(
                expectedTag,
                out var actualTag,
                out var contentLength,
                out var headerLength,
                out var contents)) {
                if (contents.Length > destination.Length) {
                    bytesWritten = 0;
                    return false;
                }

                contents.Span.CopyTo(destination);
                bytesWritten = contents.Length;
                _data = _data.Slice(headerLength + contents.Length);
                return true;
            }

            Debug.Assert(actualTag.IsConstructed);

            var copied = TryCopyConstructedOctetStringContents(
                Slice(_data, headerLength, contentLength),
                destination,
                contentLength == null,
                out var bytesRead,
                out bytesWritten);

            if (copied) {
                _data = _data.Slice(headerLength + bytesRead);
            }

            return copied;
        }

        public void ReadNull() {
            ReadNull(Asn1Tag.Null);
        }

        public void ReadNull(Asn1Tag expectedTag) {
            var tag = ReadTagAndLength(out var length, out var headerLength);
            CheckExpectedTag(tag, expectedTag, UniversalTagNumber.Null);

            // T-REC-X.690-201508 sec 8.8.1
            // T-REC-X.690-201508 sec 8.8.2
            if (tag.IsConstructed || length != 0) {
                throw new CryptographicException(SR.Cryptography_Der_Invalid_Encoding);
            }

            _data = _data.Slice(headerLength);
        }

        private static void ReadSubIdentifier(
            ReadOnlySpan<byte> source,
            out int bytesRead,
            out long? smallValue,
            out BigInteger? largeValue) {
            Debug.Assert(source.Length > 0);

            // T-REC-X.690-201508 sec 8.19.2 (last sentence)
            if (source[0] == 0x80) {
                throw new CryptographicException(SR.Cryptography_Der_Invalid_Encoding);
            }

            // First, see how long the segment is
            var end = -1;
            int idx;

            for (idx = 0; idx < source.Length; idx++) {
                // If the high bit isn't set this marks the end of the sub-identifier.
                var endOfIdentifier = (source[idx] & 0x80) == 0;

                if (endOfIdentifier) {
                    end = idx;
                    break;
                }
            }

            if (end < 0) {
                throw new CryptographicException(SR.Cryptography_Der_Invalid_Encoding);
            }

            bytesRead = end + 1;
            long accum = 0;

            // Fast path, 9 or fewer bytes => fits in a signed long.
            // (7 semantic bits per byte * 9 bytes = 63 bits, which leaves the sign bit alone)
            if (bytesRead <= 9) {
                for (idx = 0; idx < bytesRead; idx++) {
                    var cur = source[idx];
                    accum <<= 7;
                    accum |= (byte)(cur & 0x7F);
                }

                largeValue = null;
                smallValue = accum;
                return;
            }

            // Slow path, needs temporary storage.

            const int SemanticByteCount = 7;
            const int ContentByteCount = 8;

            // Every 8 content bytes turns into 7 integer bytes, so scale the count appropriately.
            // Add one while we're shrunk to account for the needed padding byte or the len%8 discarded bytes.
            var bytesRequired = ((bytesRead / ContentByteCount) + 1) * SemanticByteCount;
            var tmpBytes = ArrayPool<byte>.Shared.Rent(bytesRequired);
            // Ensure all the bytes are zeroed out for BigInteger's parsing.
            Array.Clear(tmpBytes, 0, tmpBytes.Length);

            Span<byte> writeSpan = tmpBytes;
            Span<byte> accumValueBytes = stackalloc byte[sizeof(long)];
            var nextStop = bytesRead;
            idx = bytesRead - ContentByteCount;

            while (nextStop > 0) {
                var cur = source[idx];

                accum <<= 7;
                accum |= (byte)(cur & 0x7F);

                idx++;

                if (idx >= nextStop) {
                    Debug.Assert(idx == nextStop);
                    Debug.Assert(writeSpan.Length >= SemanticByteCount);

                    BinaryPrimitives.WriteInt64LittleEndian(accumValueBytes, accum);
                    Debug.Assert(accumValueBytes[7] == 0);
                    accumValueBytes.Slice(0, SemanticByteCount).CopyTo(writeSpan);
                    writeSpan = writeSpan.Slice(SemanticByteCount);

                    accum = 0;
                    nextStop -= ContentByteCount;
                    idx = Math.Max(0, nextStop - ContentByteCount);
                }
            }

            var bytesWritten = tmpBytes.Length - writeSpan.Length;

            // Verify our bytesRequired calculation. There should be at most 7 padding bytes.
            // If the length % 8 is 7 we'll have 0 padding bytes, but the sign bit is still clear.
            //
            // 8 content bytes had a sign bit problem, so we gave it a second 7-byte block, 7 remain.
            // 7 content bytes got a single block but used and wrote 7 bytes, but only 49 of the 56 bits.
            // 6 content bytes have a padding count of 1.
            // 1 content byte has a padding count of 6.
            // 0 content bytes is illegal, but see 8 for the cycle.
            var paddingByteCount = bytesRequired - bytesWritten;
            Debug.Assert(paddingByteCount >= 0 && paddingByteCount < sizeof(long));

            largeValue = new BigInteger(tmpBytes);
            smallValue = null;

            Array.Clear(tmpBytes, 0, bytesWritten);
            ArrayPool<byte>.Shared.Return(tmpBytes);
        }

        private string ReadObjectIdentifierAsString(Asn1Tag expectedTag, out int totalBytesRead) {
            var tag = ReadTagAndLength(out var length, out var headerLength);
            CheckExpectedTag(tag, expectedTag, UniversalTagNumber.ObjectIdentifier);

            // T-REC-X.690-201508 sec 8.19.1
            // T-REC-X.690-201508 sec 8.19.2 says the minimum length is 1
            if (tag.IsConstructed || length < 1) {
                throw new CryptographicException(SR.Cryptography_Der_Invalid_Encoding);
            }

            var contentsMemory = Slice(_data, headerLength, length.Value);
            var contents = contentsMemory.Span;

            // Each byte can contribute a 3 digit value and a '.' (e.g. "126."), but usually
            // they convey one digit and a separator.
            //
            // The OID with the most arcs which were found after a 30 minute search is
            // "1.3.6.1.4.1.311.60.2.1.1" (EV cert jurisdiction of incorporation - locality)
            // which has 11 arcs.
            // The longest "known" segment is 16 bytes, a UUID-as-an-arc value.
            // 16 * 11 = 176 bytes for an "extremely long" OID.
            //
            // So pre-allocate the StringBuilder with at most 1020 characters, an input longer than
            // 255 encoded bytes will just have to re-allocate.
            var builder = new StringBuilder(((byte)contents.Length) * 4);

            ReadSubIdentifier(contents, out var bytesRead, out var smallValue, out var largeValue);

            // T-REC-X.690-201508 sec 8.19.4
            // The first two subidentifiers (X.Y) are encoded as (X * 40) + Y, because Y is
            // bounded [0, 39] for X in {0, 1}, and only X in {0, 1, 2} are legal.
            // So:
            // * identifier < 40 => X = 0, Y = identifier.
            // * identifier < 80 => X = 1, Y = identifier - 40.
            // * else: X = 2, Y = identifier - 80.
            byte firstArc;

            if (smallValue != null) {
                var firstIdentifier = smallValue.Value;

                if (firstIdentifier < 40) {
                    firstArc = 0;
                }
                else if (firstIdentifier < 80) {
                    firstArc = 1;
                    firstIdentifier -= 40;
                }
                else {
                    firstArc = 2;
                    firstIdentifier -= 80;
                }

                builder.Append(firstArc);
                builder.Append('.');
                builder.Append(firstIdentifier);
            }
            else {
                Debug.Assert(largeValue != null);
                var firstIdentifier = largeValue.Value;

                // We're only here because we were bigger than long.MaxValue, so
                // we're definitely on arc 2.
                Debug.Assert(firstIdentifier > long.MaxValue);

                firstArc = 2;
                firstIdentifier -= 80;

                builder.Append(firstArc);
                builder.Append('.');
                builder.Append(firstIdentifier.ToString());
            }

            contents = contents.Slice(bytesRead);

            while (!contents.IsEmpty) {
                ReadSubIdentifier(contents, out bytesRead, out smallValue, out largeValue);
                // Exactly one should be non-null.
                Debug.Assert(smallValue == null != (largeValue == null));

                builder.Append('.');

                if (smallValue != null) {
                    builder.Append(smallValue.Value);
                }
                else {
                    builder.Append(largeValue.Value.ToString());
                }

                contents = contents.Slice(bytesRead);
            }

            totalBytesRead = headerLength + length.Value;
            return builder.ToString();
        }

        public string ReadObjectIdentifierAsString() {
            return ReadObjectIdentifierAsString(Asn1Tag.ObjectIdentifier);
        }

        public string ReadObjectIdentifierAsString(Asn1Tag expectedTag) {
            var oidValue = ReadObjectIdentifierAsString(expectedTag, out var bytesRead);

            _data = _data.Slice(bytesRead);

            return oidValue;
        }

        public Oid ReadObjectIdentifier(bool skipFriendlyName = false) {
            return ReadObjectIdentifier(Asn1Tag.ObjectIdentifier, skipFriendlyName);
        }

        public Oid ReadObjectIdentifier(Asn1Tag expectedTag, bool skipFriendlyName = false) {
            var oidValue = ReadObjectIdentifierAsString(expectedTag, out var bytesRead);
            var oid = skipFriendlyName ? new Oid(oidValue, oidValue) : new Oid(oidValue);

            // Don't slice until the return object has been created.
            _data = _data.Slice(bytesRead);

            return oid;
        }

        // T-REC-X.690-201508 sec 8.23
        private bool TryCopyCharacterStringBytes(
            Asn1Tag expectedTag,
            UniversalTagNumber universalTagNumber,
            Span<byte> destination,
            out int bytesRead,
            out int bytesWritten) {
            // T-REC-X.690-201508 sec 8.23.3, all character strings are encoded as octet strings.
            if (TryGetPrimitiveOctetStringBytes(
                expectedTag,
                out var actualTag,
                out var contentLength,
                out var headerLength,
                out var contents,
                universalTagNumber)) {
                bytesWritten = contents.Length;

                if (destination.Length < bytesWritten) {
                    bytesWritten = 0;
                    bytesRead = 0;
                    return false;
                }

                contents.Span.CopyTo(destination);
                bytesRead = headerLength + bytesWritten;
                return true;
            }

            Debug.Assert(actualTag.IsConstructed);

            var copied = TryCopyConstructedOctetStringContents(
                Slice(_data, headerLength, contentLength),
                destination,
                contentLength == null,
                out var contentBytesRead,
                out bytesWritten);

            if (copied) {
                bytesRead = headerLength + contentBytesRead;
            }
            else {
                bytesRead = 0;
            }

            return copied;
        }

        private static unsafe bool TryCopyCharacterString(
            ReadOnlySpan<byte> source,
            Span<char> destination,
            Encoding encoding,
            out int charsWritten) {
            if (source.Length == 0) {
                charsWritten = 0;
                return true;
            }

            fixed (byte* bytePtr = &MemoryMarshal.GetReference(source))
            fixed (char* charPtr = &MemoryMarshal.GetReference(destination)) {
                try {
                    var charCount = encoding.GetCharCount(bytePtr, source.Length);

                    if (charCount > destination.Length) {
                        charsWritten = 0;
                        return false;
                    }

                    charsWritten = encoding.GetChars(bytePtr, source.Length, charPtr, destination.Length);
                    Debug.Assert(charCount == charsWritten);
                }
                catch (DecoderFallbackException e) {
                    throw new CryptographicException(SR.Cryptography_Der_Invalid_Encoding, e);
                }

                return true;
            }
        }

        private string GetCharacterString(
            Asn1Tag expectedTag,
            UniversalTagNumber universalTagNumber,
            Encoding encoding) {
            byte[] rented = null;

            // T-REC-X.690-201508 sec 8.23.3, all character strings are encoded as octet strings.
            var contents = GetOctetStringContents(
                expectedTag,
                universalTagNumber,
                out var bytesRead,
                ref rented);

            try {
                string str;

                if (contents.Length == 0) {
                    str = string.Empty;
                }
                else {
                    unsafe {
                        fixed (byte* bytePtr = &MemoryMarshal.GetReference(contents)) {
                            try {
                                str = encoding.GetString(bytePtr, contents.Length);
                            }
                            catch (DecoderFallbackException e) {
                                throw new CryptographicException(SR.Cryptography_Der_Invalid_Encoding, e);
                            }
                        }
                    }
                }

                _data = _data.Slice(bytesRead);
                return str;
            }
            finally {
                if (rented != null) {
                    Array.Clear(rented, 0, contents.Length);
                    ArrayPool<byte>.Shared.Return(rented);
                }
            }
        }

        private bool TryCopyCharacterString(
            Asn1Tag expectedTag,
            UniversalTagNumber universalTagNumber,
            Encoding encoding,
            Span<char> destination,
            out int charsWritten) {
            byte[] rented = null;

            // T-REC-X.690-201508 sec 8.23.3, all character strings are encoded as octet strings.
            var contents = GetOctetStringContents(
                expectedTag,
                universalTagNumber,
                out var bytesRead,
                ref rented);

            try {
                var copied = TryCopyCharacterString(
                    contents,
                    destination,
                    encoding,
                    out charsWritten);

                if (copied) {
                    _data = _data.Slice(bytesRead);
                }

                return copied;
            }
            finally {
                if (rented != null) {
                    Array.Clear(rented, 0, contents.Length);
                    ArrayPool<byte>.Shared.Return(rented);
                }
            }
        }

        /// <summary>
        /// Gets the source data for a character string under a primitive encoding and tagged as
        /// the universal class tag for the encoding type.
        /// </summary>
        /// <param name="encodingType">The UniversalTagNumber for the string encoding type.</param>
        /// <param name="contents">The content bytes for the UTF8String payload.</param>
        /// <returns>
        ///   <c>true</c> if the character string uses a primitive encoding, <c>false</c> otherwise.
        /// </returns>
        /// <exception cref="CryptographicException">
        ///  <ul>
        ///   <li>No data remains</li>
        ///   <li>The tag read does not match the expected tag</li>
        ///   <li>The length is invalid under the chosen encoding rules</li>
        ///   <li>A CER encoding was chosen and the primitive content length exceeds the maximum allowed</li>
        /// </ul>
        /// </exception>
        public bool TryGetPrimitiveCharacterStringBytes(UniversalTagNumber encodingType, out ReadOnlyMemory<byte> contents) {
            return TryGetPrimitiveCharacterStringBytes(new Asn1Tag(encodingType), encodingType, out contents);
        }

        /// <summary>
        /// Gets the uninterpreted contents for a character string under a primitive encoding.
        /// The contents are not validated as belonging to the requested encoding type.
        /// </summary>
        /// <param name="expectedTag">The expected tag</param>
        /// <param name="encodingType">The UniversalTagNumber for the string encoding type.</param>
        /// <param name="contents">The contents for the character string.</param>
        /// <returns>
        ///   <c>true</c> if the character string uses a primitive encoding, <c>false</c> otherwise.
        /// </returns>
        /// <exception cref="CryptographicException">
        ///  <ul>
        ///   <li>No data remains</li>
        ///   <li>The tag read does not match the expected tag</li>
        ///   <li>The length is invalid under the chosen encoding rules</li>
        ///   <li>A CER encoding was chosen and the primitive content length exceeds the maximum allowed</li>
        /// </ul>
        /// </exception>
        /// <exception cref="ArgumentOutOfRangeException">
        ///   <paramref name="encodingType"/> is not a known character string encoding type.
        /// </exception>
        public bool TryGetPrimitiveCharacterStringBytes(
            Asn1Tag expectedTag,
            UniversalTagNumber encodingType,
            out ReadOnlyMemory<byte> contents) {
            CheckCharacterStringEncodingType(encodingType);

            // T-REC-X.690-201508 sec 8.23.3, all character strings are encoded as octet strings.
            return TryGetPrimitiveOctetStringBytes(expectedTag, encodingType, out contents);
        }

        public bool TryCopyCharacterStringBytes(
            UniversalTagNumber encodingType,
            Span<byte> destination,
            out int bytesWritten) {
            return TryCopyCharacterStringBytes(
                new Asn1Tag(encodingType),
                encodingType,
                destination,
                out bytesWritten);
        }

        public bool TryCopyCharacterStringBytes(
            Asn1Tag expectedTag,
            UniversalTagNumber encodingType,
            Span<byte> destination,
            out int bytesWritten) {
            CheckCharacterStringEncodingType(encodingType);

            var copied = TryCopyCharacterStringBytes(
                expectedTag,
                encodingType,
                destination,
                out var bytesRead,
                out bytesWritten);

            if (copied) {
                _data = _data.Slice(bytesRead);
            }

            return copied;
        }

        public bool TryCopyCharacterString(
            UniversalTagNumber encodingType,
            Span<char> destination,
            out int charsWritten) {
            return TryCopyCharacterString(
                new Asn1Tag(encodingType),
                encodingType,
                destination,
                out charsWritten);
        }

        public bool TryCopyCharacterString(
            Asn1Tag expectedTag,
            UniversalTagNumber encodingType,
            Span<char> destination,
            out int charsWritten) {
            var encoding = AsnCharacterStringEncodings.GetEncoding(encodingType);
            return TryCopyCharacterString(expectedTag, encodingType, encoding, destination, out charsWritten);
        }

        public string GetCharacterString(UniversalTagNumber encodingType) {
            return GetCharacterString(new Asn1Tag(encodingType), encodingType);
        }

        public string GetCharacterString(Asn1Tag expectedTag, UniversalTagNumber encodingType) {
            var encoding = AsnCharacterStringEncodings.GetEncoding(encodingType);
            return GetCharacterString(expectedTag, encodingType, encoding);
        }

        public AsnReader ReadSequence() {
            return ReadSequence(Asn1Tag.Sequence);
        }

        public AsnReader ReadSequence(Asn1Tag expectedTag) {
            var tag = ReadTagAndLength(out var length, out var headerLength);
            CheckExpectedTag(tag, expectedTag, UniversalTagNumber.Sequence);

            // T-REC-X.690-201508 sec 8.9.1
            // T-REC-X.690-201508 sec 8.10.1
            if (!tag.IsConstructed) {
                throw new CryptographicException(SR.Cryptography_Der_Invalid_Encoding);
            }

            var suffix = 0;

            if (length == null) {
                length = SeekEndOfContents(_data.Slice(headerLength));
                suffix = kEndOfContentsEncodedLength;
            }

            var contents = Slice(_data, headerLength, length.Value);

            _data = _data.Slice(headerLength + contents.Length + suffix);
            return new AsnReader(contents, _ruleSet);
        }

        /// <summary>
        /// Builds a new AsnReader over the bytes bounded by the current position which
        /// corresponds to an ASN.1 SET OF value, validating the CER or DER sort ordering
        /// unless suppressed.
        /// </summary>
        /// <param name="skipSortOrderValidation">
        ///   <c>false</c> to validate the sort ordering of the contents, <c>true</c> to
        ///   allow reading the data without verifying it was properly sorted by the writer.
        /// </param>
        /// <returns>An AsnReader over the current position, bounded by the contained length value.</returns>
        public AsnReader ReadSetOf(bool skipSortOrderValidation = false) {
            return ReadSetOf(Asn1Tag.SetOf, skipSortOrderValidation);
        }

        public AsnReader ReadSetOf(Asn1Tag expectedTag, bool skipSortOrderValidation = false) {
            var tag = ReadTagAndLength(out var length, out var headerLength);
            CheckExpectedTag(tag, expectedTag, UniversalTagNumber.SetOf);

            // T-REC-X.690-201508 sec 8.12.1
            if (!tag.IsConstructed) {
                throw new CryptographicException(SR.Cryptography_Der_Invalid_Encoding);
            }

            var suffix = 0;

            if (length == null) {
                length = SeekEndOfContents(_data.Slice(headerLength));
                suffix = kEndOfContentsEncodedLength;
            }

            var contents = Slice(_data, headerLength, length.Value);

            if (!skipSortOrderValidation) {
                // T-REC-X.690-201508 sec 11.6
                // BER data is not required to be sorted.
                if (_ruleSet == AsnEncodingRules.DER ||
                    _ruleSet == AsnEncodingRules.CER) {
                    var reader = new AsnReader(contents, _ruleSet);
                    var current = ReadOnlyMemory<byte>.Empty;
                    var comparer = SetOfValueComparer.Instance;

                    while (reader.HasData) {
                        var previous = current;
                        current = reader.GetEncodedValue();

                        if (comparer.Compare(current, previous) < 0) {
                            throw new CryptographicException(SR.Cryptography_Der_Invalid_Encoding);
                        }
                    }
                }
            }

            _data = _data.Slice(headerLength + contents.Length + suffix);
            return new AsnReader(contents, _ruleSet);
        }

        private ReadOnlySpan<byte> GetOctetStringContents(
            Asn1Tag expectedTag,
            UniversalTagNumber universalTagNumber,
            out int bytesRead,
            ref byte[] rented,
            Span<byte> tmpSpace = default) {
            Debug.Assert(rented == null);

            if (TryGetPrimitiveOctetStringBytes(
                expectedTag,
                out var actualTag,
                out var contentLength,
                out var headerLength,
                out var contentsOctets,
                universalTagNumber)) {
                bytesRead = headerLength + contentsOctets.Length;
                return contentsOctets.Span;
            }

            Debug.Assert(actualTag.IsConstructed);

            var source = Slice(_data, headerLength, contentLength);
            var isIndefinite = contentLength == null;
            var octetStringLength = CountConstructedOctetString(source, isIndefinite);

            if (tmpSpace.Length < octetStringLength) {
                rented = ArrayPool<byte>.Shared.Rent(octetStringLength);
                tmpSpace = rented;
            }

            CopyConstructedOctetString(source, tmpSpace, isIndefinite, out var localBytesRead, out var bytesWritten);
            Debug.Assert(bytesWritten == octetStringLength);

            bytesRead = headerLength + localBytesRead;
            return tmpSpace.Slice(0, bytesWritten);
        }

        private static ReadOnlySpan<byte> SliceAtMost(ReadOnlySpan<byte> source, int longestPermitted) {
            var len = Math.Min(longestPermitted, source.Length);
            return source.Slice(0, len);
        }

        private static ReadOnlySpan<byte> Slice(ReadOnlySpan<byte> source, int offset, int length) {
            Debug.Assert(offset >= 0);

            if (length < 0 || source.Length - offset < length) {
                throw new CryptographicException(SR.Cryptography_Der_Invalid_Encoding);
            }

            return source.Slice(offset, length);
        }

        private static ReadOnlyMemory<byte> Slice(ReadOnlyMemory<byte> source, int offset, int? length) {
            Debug.Assert(offset >= 0);

            if (length == null) {
                return source.Slice(offset);
            }

            var lengthVal = length.Value;

            if (lengthVal < 0 || source.Length - offset < lengthVal) {
                throw new CryptographicException(SR.Cryptography_Der_Invalid_Encoding);
            }

            return source.Slice(offset, lengthVal);
        }

        private static void CheckEncodingRules(AsnEncodingRules ruleSet) {
            if (ruleSet != AsnEncodingRules.BER &&
                ruleSet != AsnEncodingRules.CER &&
                ruleSet != AsnEncodingRules.DER) {
                throw new ArgumentOutOfRangeException(nameof(ruleSet));
            }
        }

        private static void CheckExpectedTag(Asn1Tag tag, Asn1Tag expectedTag, UniversalTagNumber tagNumber) {
            if (expectedTag.TagClass == TagClass.Universal && expectedTag.TagValue != (int)tagNumber) {
                throw new ArgumentException(
                    SR.Cryptography_Asn_UniversalValueIsFixed,
                    nameof(expectedTag));
            }

            if (expectedTag.TagClass != tag.TagClass || expectedTag.TagValue != tag.TagValue) {
                throw new CryptographicException(SR.Cryptography_Der_Invalid_Encoding);
            }
        }

        private static void CheckCharacterStringEncodingType(UniversalTagNumber encodingType) {
            // T-REC-X.680-201508 sec 41
            switch (encodingType) {
                case UniversalTagNumber.BMPString:
                case UniversalTagNumber.GeneralString:
                case UniversalTagNumber.GraphicString:
                case UniversalTagNumber.IA5String:
                case UniversalTagNumber.ISO646String:
                case UniversalTagNumber.NumericString:
                case UniversalTagNumber.PrintableString:
                case UniversalTagNumber.TeletexString:
                // T61String is an alias for TeletexString (already listed)
                case UniversalTagNumber.UniversalString:
                case UniversalTagNumber.UTF8String:
                case UniversalTagNumber.VideotexString:
                    // VisibleString is an alias for ISO646String (already listed)
                    return;
            }

            throw new ArgumentOutOfRangeException(nameof(encodingType));
        }
    }
}
