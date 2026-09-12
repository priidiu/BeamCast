using System.Buffers.Binary;

namespace AirNext.Core.Audio;

/// <summary>
/// Enkoder ALAC (Apple Lossless) — wierny port C# oryginalnego ALACEncoder.cpp Apple
/// (Apache-2.0, https://github.com/macosforge/alac). Bit-exact: output zgodny bajt-w-bajt
/// z referencyjnym enkoderem (weryfikowane testami przeciw golden wektorowi).
///
/// Supports: stereo 16-bit (exactly format AirPlay ALAC 16/44.1 z docs/04).
/// Nie handles: mono, 20/24/32-bit, multi-channel (poza zakresem MVP).
/// </summary>
public sealed class AlacEncoder
{
 // ---- Constants z ALACAudioTypes.h / ALACEncoder.cpp / aglib.h / dplib.h ----
    private const int kALACDefaultFrameSize = 4096;
    private const int kALACMaxChannels = 8;
    private const int kALACMaxSearches = 16;
    private const int kALACMaxCoefs = 16;
    private const int kDefaultMixBits = 2;
    private const int kDefaultMixRes = 0;
    private const int kMaxRes = 4;
    private const int kDefaultNumUV = 8;
    private const int kMinUV = 4;
    private const int kMaxUV = 8;
    private const int kMaxSampleSize = 32;
    private const int DENSHIFT_DEFAULT = 9;
    private const int AINIT = 38;
    private const int BINIT = -29;
    private const int CINIT = -2;

    // aglib.h
    private const int QBSHIFT = 9;
    private const int QB = 1 << QBSHIFT;
    private const int PB0 = 40;
    private const int MB0 = 10;
    private const int KB0 = 14;
    private const int MAX_RUN_DEFAULT = 255;
    private const int MMULSHIFT = 2;
    private const int MDENSHIFT = QBSHIFT - MMULSHIFT - 1;
    private const int MOFF = 1 << (MDENSHIFT - 2);
    private const int BITOFF = 24;
    private const int MAX_PREFIX_16 = 9;
    private const int MAX_DATATYPE_BITS_16 = 16;
    private const int MAX_PREFIX_32 = 9;
    private const int N_MAX_MEAN_CLAMP = 0xffff;
    private const int N_MEAN_CLAMP_VAL = 0xffff;

 // ID elements (ALACBitUtilities.h)
    private const uint ID_SCE = 0;
    private const uint ID_CPE = 1;
    private const uint ID_LFE = 3;
    private const uint ID_END = 7;

    private readonly int _frameSize;
    private readonly int _sampleRate;
    private readonly int _numChannels = 2;
    private readonly int _bitDepth = 16;

    // Stan enkodera (jak w C++: mLastMixRes, mCoefsU/V, bufory)
    private readonly short[] _lastMixRes = new short[kALACMaxChannels];
    private readonly short[][][] _coefsU = new short[kALACMaxChannels][][];
    private readonly short[][][] _coefsV = new short[kALACMaxChannels][][];

    private int _maxFrameBytes;
    private int _maxOutputBytes;

 /// <summary>Maksymalny rozmiar ramki output (do alokacji bufora).</summary>
    public int MaxOutputBytes => _maxOutputBytes;

 /// <summary>Create enkoder ALAC 16-bit stereo.</summary>
    public AlacEncoder(int sampleRate = 44100, int frameSize = kALACDefaultFrameSize)
    {
        _sampleRate = sampleRate;
        _frameSize = frameSize;
        _maxOutputBytes = _frameSize * _numChannels * ((10 + kMaxSampleSize) / 8) + 1;
        for (int c = 0; c < kALACMaxChannels; c++)
        {
            _coefsU[c] = new short[kALACMaxSearches][];
            _coefsV[c] = new short[kALACMaxSearches][];
            for (int s = 0; s < kALACMaxSearches; s++)
            {
                _coefsU[c][s] = new short[kALACMaxCoefs];
                _coefsV[c][s] = new short[kALACMaxCoefs];
            }
        }
        InitCoefs();
    }

    private void InitCoefs()
    {
        for (int channel = 0; channel < _numChannels; channel++)
        {
            for (int search = 0; search < kALACMaxSearches; search++)
            {
                InitCoefsOne(_coefsU[channel][search]);
                InitCoefsOne(_coefsV[channel][search]);
            }
        }
    }

    private static void InitCoefsOne(short[] coefs)
    {
        int den = 1 << DENSHIFT_DEFAULT;
        coefs[0] = (short)((AINIT * den) >> 4);
        coefs[1] = (short)((BINIT * den) >> 4);
        coefs[2] = (short)((CINIT * den) >> 4);
        for (int k = 3; k < kALACMaxCoefs; k++)
            coefs[k] = 0;
    }

    /// <summary>
 /// Enkoduje one frame interleaved int16 stereo do ALAC.
 /// Zwraca count zapisanych bytes (lub -1 przy error).
    /// </summary>
    public int EncodeFrame(ReadOnlySpan<short> pcmStereo, Span<byte> output)
    {
        int numFrames = pcmStereo.Length / 2;
        if (numFrames == 0 || numFrames > _frameSize)
            return -1;

        var bits = new BitWriter(output, _maxOutputBytes);

        // ID_CPE (3 bity) + element instance tag (4 bity = 0)
        bits.Write(ID_CPE, 3);
        bits.Write(0, 4);

        EncodeStereo(bits, pcmStereo, 2, 0, numFrames);

        // ID_END + byte-align z zerami
        bits.Write(ID_END, 3);
        bits.ByteAlign(addZeros: true);

        int outputSize = bits.PositionBytes;
        _maxFrameBytes = Math.Max(_maxFrameBytes, outputSize);

 // Skopiuj z internal bufora do przekazanego output
        bits.Buffer.AsSpan(0, outputSize).CopyTo(output);
        return outputSize;
    }

    /// <summary>Magic cookie ALAC (24 B dla stereo) — do SDP `a=fmtp:96 ...`.</summary>
    public byte[] GetMagicCookie()
    {
        var cookie = new byte[24];
        BinaryPrimitives.WriteUInt32BigEndian(cookie.AsSpan(0), (uint)_frameSize);
        cookie[4] = 0;                                  // compatibleVersion
        cookie[5] = (byte)_bitDepth;
        cookie[6] = (byte)PB0;
        cookie[7] = (byte)MB0;
        cookie[8] = (byte)KB0;
        cookie[9] = (byte)_numChannels;
        BinaryPrimitives.WriteUInt16BigEndian(cookie.AsSpan(10), MAX_RUN_DEFAULT);
        BinaryPrimitives.WriteUInt32BigEndian(cookie.AsSpan(12), (uint)_maxFrameBytes);
        BinaryPrimitives.WriteUInt32BigEndian(cookie.AsSpan(16), 0); // avgBitRate
        BinaryPrimitives.WriteUInt32BigEndian(cookie.AsSpan(20), (uint)_sampleRate);
        return cookie;
    }

    // =====================================================================
    // EncodeStereo — port ALACEncoder::EncodeStereo (bitdepth 16, mode 0)
    // =====================================================================
    private void EncodeStereo(BitWriter bits, ReadOnlySpan<short> input, int stride, int channelIndex, int numSamples)
    {
        var workBits = new BitWriter(bits.Buffer, _maxOutputBytes);
        var mixU = new int[_frameSize];
        var mixV = new int[_frameSize];
        var predU = new int[_frameSize];
        var predV = new int[_frameSize];
        var shiftUV = new ushort[_frameSize * 2];

        // 16-bit: bytesShifted=0, chanBits=17
        int bytesShifted = 0;
        int chanBits = _bitDepth - (bytesShifted * 8) + 1;
        int partialFrame = (numSamples == _frameSize) ? 0 : 1;

        int mixBits = kDefaultMixBits;
        int maxRes = kMaxRes;
        int numU = kDefaultNumUV, numV = kDefaultNumUV;
        int mode = 0;
        int pbFactor = 4;
        int dilate = 8;

        uint minBits, minBits1, minBits2;
        minBits = minBits1 = minBits2 = 1u << 31;

        // Brute-force search mixRes
        int bestRes = _lastMixRes[channelIndex];
        for (int mixRes = 0; mixRes <= maxRes; mixRes++)
        {
            Mix16(input, stride, mixU, mixV, numSamples / dilate, mixBits, mixRes);
            workBits.Reset();
            PcBlock(mixU, predU, numSamples / dilate, GetCoefs(_coefsU, channelIndex, numU - 1), numU, chanBits, DENSHIFT_DEFAULT);
            PcBlock(mixV, predV, numSamples / dilate, GetCoefs(_coefsV, channelIndex, numV - 1), numV, chanBits, DENSHIFT_DEFAULT);
            DynComp(workBits, predU, numSamples / dilate, chanBits, out uint bits1, pbFactor);
            DynComp(workBits, predV, numSamples / dilate, chanBits, out uint bits2, pbFactor);

            if ((bits1 + bits2) < minBits1)
            {
                minBits1 = bits1 + bits2;
                bestRes = mixRes;
            }
        }
        _lastMixRes[channelIndex] = (short)bestRes;

        // Final mix z bestRes
        int mixResFinal = _lastMixRes[channelIndex];
        Mix16(input, stride, mixU, mixV, numSamples, mixBits, mixResFinal);

        // Predictor coefficient search loop (numUV 4..8 krok 4)
        numU = numV = kMinUV;
        minBits1 = minBits2 = 1u << 31;

        for (int numUV = kMinUV; numUV <= kMaxUV; numUV += 4)
        {
            workBits.Reset();
            dilate = 32;
            for (int converge = 0; converge < 8; converge++)
            {
                PcBlock(mixU, predU, numSamples / dilate, GetCoefs(_coefsU, channelIndex, numUV - 1), numUV, chanBits, DENSHIFT_DEFAULT);
                PcBlock(mixV, predV, numSamples / dilate, GetCoefs(_coefsV, channelIndex, numUV - 1), numUV, chanBits, DENSHIFT_DEFAULT);
            }
            dilate = 8;
            DynComp(workBits, predU, numSamples / dilate, chanBits, out uint b1, pbFactor);
            if ((b1 * (uint)dilate + 16u * (uint)numUV) < minBits1)
            {
                minBits1 = b1 * (uint)dilate + 16u * (uint)numUV;
                numU = numUV;
            }
            DynComp(workBits, predV, numSamples / dilate, chanBits, out uint b2, pbFactor);
            if ((b2 * (uint)dilate + 16u * (uint)numUV) < minBits2)
            {
                minBits2 = b2 * (uint)dilate + 16u * (uint)numUV;
                numV = numUV;
            }
        }

        // Escape check
        minBits = minBits1 + minBits2 + (8 * 8) + ((partialFrame == 1) ? 32u : 0u);
        uint escapeBits = (uint)(numSamples * _bitDepth * 2) + ((partialFrame == 1) ? 32u : 0u) + (2 * 8);
        bool doEscape = minBits >= escapeBits;

        if (!doEscape)
        {
            // Header
            bits.Write(0, 12);
            bits.Write((uint)((partialFrame << 3) | (bytesShifted << 1)), 4);
            if (partialFrame == 1)
                bits.Write((uint)numSamples, 32);
            bits.Write((uint)mixBits, 8);
            bits.Write((uint)mixResFinal, 8);
            bits.Write((uint)((mode << 4) | DENSHIFT_DEFAULT), 8);
            bits.Write((uint)((pbFactor << 5) | numU), 8);
            for (int index = 0; index < numU; index++)
                bits.Write((ushort)GetCoefs(_coefsU, channelIndex, numU - 1)[index], 16);

            bits.Write((uint)((mode << 4) | DENSHIFT_DEFAULT), 8);
            bits.Write((uint)((pbFactor << 5) | numV), 8);
            for (int index = 0; index < numV; index++)
                bits.Write((ushort)GetCoefs(_coefsV, channelIndex, numV - 1)[index], 16);

            if (bytesShifted != 0)
            {
                int bitShift = bytesShifted * 8;
                for (int index = 0; index < (numSamples * 2); index += 2)
                {
                    uint shiftedVal = ((uint)shiftUV[index] << bitShift) | (uint)shiftUV[index + 1];
                    bits.Write(shiftedVal, bitShift * 2);
                }
            }

            // Left channel
            PcBlock(mixU, predU, numSamples, GetCoefs(_coefsU, channelIndex, numU - 1), numU, chanBits, DENSHIFT_DEFAULT);
            DynComp(bits, predU, numSamples, chanBits, out _, pbFactor);

            // Right channel
            PcBlock(mixV, predV, numSamples, GetCoefs(_coefsV, channelIndex, numV - 1), numV, chanBits, DENSHIFT_DEFAULT);
            DynComp(bits, predV, numSamples, chanBits, out _, pbFactor);

 // If mimo wszystko za large → escape
            uint finalBits = (uint)bits.PositionBits;
            if (finalBits >= escapeBits)
            {
                bits.Reset();
                EncodeStereoEscape(bits, input, stride, numSamples);
            }
        }
        else
        {
            EncodeStereoEscape(bits, input, stride, numSamples);
        }
    }

    private void EncodeStereoEscape(BitWriter bits, ReadOnlySpan<short> input, int stride, int numSamples)
    {
        int partialFrame = (numSamples == _frameSize) ? 0 : 1;
        bits.Write(0, 12);
        bits.Write((uint)((partialFrame << 3) | 1), 4); // LSB=1: frame niekompresowany
        if (partialFrame == 1)
            bits.Write((uint)numSamples, 32);

        for (int index = 0; index < (numSamples * stride); index += stride)
        {
            bits.Write((ushort)input[index], 16);
            bits.Write((ushort)input[index + 1], 16);
        }
    }

    // =====================================================================
    // mix16 — port matrix_enc.c
    // =====================================================================
    private static void Mix16(ReadOnlySpan<short> input, int stride, int[] u, int[] v, int numSamples, int mixbits, int mixres)
    {
        if (mixres != 0)
        {
            int mod = 1 << mixbits;
            int m2 = mod - mixres;
            for (int j = 0; j < numSamples; j++)
            {
                int l = input[j * stride];
                int r = input[j * stride + 1];
                u[j] = (mixres * l + m2 * r) >> mixbits;
                v[j] = l - r;
            }
        }
        else
        {
            for (int j = 0; j < numSamples; j++)
            {
                u[j] = input[j * stride];
                v[j] = input[j * stride + 1];
            }
        }
    }

    // =====================================================================
    // pc_block — port dp_enc.c (numactive 4/8/general; 31 = short-circuit)
    // =====================================================================
    private static void PcBlock(int[] input, int[] pc1, int num, short[] coefs, int numactive, int chanbits, int denshift)
    {
        int chanshift = 32 - chanbits;
        int denhalf = 1 << (denshift - 1);

        pc1[0] = input[0];
        if (numactive == 0)
        {
            if (num > 1)
                Array.Copy(input, 1, pc1, 1, num - 1);
            return;
        }
        if (numactive == 31)
        {
            for (int j = 1; j < num; j++)
            {
                int del = input[j] - input[j - 1];
                pc1[j] = (del << chanshift) >> chanshift;
            }
            return;
        }

        for (int j = 1; j <= numactive; j++)
        {
            int del = input[j] - input[j - 1];
            pc1[j] = (del << chanshift) >> chanshift;
        }

        int lim = numactive + 1;

        if (numactive == 4)
        {
            int a0 = coefs[0], a1 = coefs[1], a2 = coefs[2], a3 = coefs[3];
            for (int j = lim; j < num; j++)
            {
                int top = input[j - lim];
                int b0 = top - input[j - 1];
                int b1 = top - input[j - 2];
                int b2 = top - input[j - 3];
                int b3 = top - input[j - 4];

                int sum1 = (denhalf - a0 * b0 - a1 * b1 - a2 * b2 - a3 * b3) >> denshift;
                int del = input[j] - top - sum1;
                del = (del << chanshift) >> chanshift;
                pc1[j] = del;
                int del0 = del;

                int sg = SignOfInt(del);
                if (sg > 0)
                {
                    int sgn = SignOfInt(b3);
                    a3 -= (short)sgn;
                    del0 -= (4 - 3) * ((sgn * b3) >> denshift);
                    if (del0 <= 0) continue;
                    sgn = SignOfInt(b2);
                    a2 -= (short)sgn;
                    del0 -= (4 - 2) * ((sgn * b2) >> denshift);
                    if (del0 <= 0) continue;
                    sgn = SignOfInt(b1);
                    a1 -= (short)sgn;
                    del0 -= (4 - 1) * ((sgn * b1) >> denshift);
                    if (del0 <= 0) continue;
                    a0 -= (short)SignOfInt(b0);
                }
                else if (sg < 0)
                {
                    int sgn = -SignOfInt(b3);
                    a3 -= (short)sgn;
                    del0 -= (4 - 3) * ((sgn * b3) >> denshift);
                    if (del0 >= 0) continue;
                    sgn = -SignOfInt(b2);
                    a2 -= (short)sgn;
                    del0 -= (4 - 2) * ((sgn * b2) >> denshift);
                    if (del0 >= 0) continue;
                    sgn = -SignOfInt(b1);
                    a1 -= (short)sgn;
                    del0 -= (4 - 1) * ((sgn * b1) >> denshift);
                    if (del0 >= 0) continue;
                    a0 += (short)SignOfInt(b0);
                }
            }
            coefs[0] = (short)a0;
            coefs[1] = (short)a1;
            coefs[2] = (short)a2;
            coefs[3] = (short)a3;
        }
        else if (numactive == 8)
        {
            int a0 = coefs[0], a1 = coefs[1], a2 = coefs[2], a3 = coefs[3];
            int a4 = coefs[4], a5 = coefs[5], a6 = coefs[6], a7 = coefs[7];
            for (int j = lim; j < num; j++)
            {
                int top = input[j - lim];
                int b0 = top - input[j - 1];
                int b1 = top - input[j - 2];
                int b2 = top - input[j - 3];
                int b3 = top - input[j - 4];
                int b4 = top - input[j - 5];
                int b5 = top - input[j - 6];
                int b6 = top - input[j - 7];
                int b7 = top - input[j - 8];

                int sum1 = (denhalf - a0 * b0 - a1 * b1 - a2 * b2 - a3 * b3
                                - a4 * b4 - a5 * b5 - a6 * b6 - a7 * b7) >> denshift;
                int del = input[j] - top - sum1;
                del = (del << chanshift) >> chanshift;
                pc1[j] = del;
                int del0 = del;

                int sg = SignOfInt(del);
                if (sg > 0)
                {
                    int sgn = SignOfInt(b7); a7 -= (short)sgn; del0 -= 1 * ((sgn * b7) >> denshift); if (del0 <= 0) continue;
                    sgn = SignOfInt(b6); a6 -= (short)sgn; del0 -= 2 * ((sgn * b6) >> denshift); if (del0 <= 0) continue;
                    sgn = SignOfInt(b5); a5 -= (short)sgn; del0 -= 3 * ((sgn * b5) >> denshift); if (del0 <= 0) continue;
                    sgn = SignOfInt(b4); a4 -= (short)sgn; del0 -= 4 * ((sgn * b4) >> denshift); if (del0 <= 0) continue;
                    sgn = SignOfInt(b3); a3 -= (short)sgn; del0 -= 5 * ((sgn * b3) >> denshift); if (del0 <= 0) continue;
                    sgn = SignOfInt(b2); a2 -= (short)sgn; del0 -= 6 * ((sgn * b2) >> denshift); if (del0 <= 0) continue;
                    sgn = SignOfInt(b1); a1 -= (short)sgn; del0 -= 7 * ((sgn * b1) >> denshift); if (del0 <= 0) continue;
                    a0 -= (short)SignOfInt(b0);
                }
                else if (sg < 0)
                {
                    int sgn = -SignOfInt(b7); a7 -= (short)sgn; del0 -= 1 * ((sgn * b7) >> denshift); if (del0 >= 0) continue;
                    sgn = -SignOfInt(b6); a6 -= (short)sgn; del0 -= 2 * ((sgn * b6) >> denshift); if (del0 >= 0) continue;
                    sgn = -SignOfInt(b5); a5 -= (short)sgn; del0 -= 3 * ((sgn * b5) >> denshift); if (del0 >= 0) continue;
                    sgn = -SignOfInt(b4); a4 -= (short)sgn; del0 -= 4 * ((sgn * b4) >> denshift); if (del0 >= 0) continue;
                    sgn = -SignOfInt(b3); a3 -= (short)sgn; del0 -= 5 * ((sgn * b3) >> denshift); if (del0 >= 0) continue;
                    sgn = -SignOfInt(b2); a2 -= (short)sgn; del0 -= 6 * ((sgn * b2) >> denshift); if (del0 >= 0) continue;
                    sgn = -SignOfInt(b1); a1 -= (short)sgn; del0 -= 7 * ((sgn * b1) >> denshift); if (del0 >= 0) continue;
                    a0 += (short)SignOfInt(b0);
                }
            }
            coefs[0] = (short)a0; coefs[1] = (short)a1; coefs[2] = (short)a2; coefs[3] = (short)a3;
            coefs[4] = (short)a4; coefs[5] = (short)a5; coefs[6] = (short)a6; coefs[7] = (short)a7;
        }
        else
        {
            // general case
            for (int j = lim; j < num; j++)
            {
                int top = input[j - lim];
                int sum1 = 0;
                for (int k = 0; k < numactive; k++)
                    sum1 -= coefs[k] * (top - input[j - 1 - k]);

                int del = input[j] - top - ((sum1 + denhalf) >> denshift);
                del = (del << chanshift) >> chanshift;
                pc1[j] = del;
                int del0 = del;

                int sg = SignOfInt(del);
                if (sg > 0)
                {
                    for (int k = numactive - 1; k >= 0; k--)
                    {
                        int dd = top - input[j - 1 - k];
                        int sgn = SignOfInt(dd);
                        coefs[k] -= (short)sgn;
                        del0 -= (numactive - k) * ((sgn * dd) >> denshift);
                        if (del0 <= 0) break;
                    }
                }
                else if (sg < 0)
                {
                    for (int k = numactive - 1; k >= 0; k--)
                    {
                        int dd = top - input[j - 1 - k];
                        int sgn = SignOfInt(dd);
                        coefs[k] += (short)sgn;
                        del0 -= (numactive - k) * ((-sgn * dd) >> denshift);
                        if (del0 >= 0) break;
                    }
                }
            }
        }
    }

    // =====================================================================
    // dyn_comp — port ag_enc.c (adaptive Golomb-Rice)
    // =====================================================================
    private void DynComp(BitWriter bits, int[] pc, int numSamples, int bitSize, out uint outNumBits, int pbFactor)
    {
 // W C++ dyn_comp pisze przez dyn_jam_noDeref directly do bufora,
 // tracking bitPos lokalnie (startPos + inkrementy). Na end BitBufferAdvance(bitstream, *outNumBits).
        int startPos = bits.PositionBits;
        int bitPos = startPos;
        int mb = MB0;
        int pb = (pbFactor * PB0) / 4;
        int kb = KB0;
        int wb = (1 << kb) - 1;
        int zmode = 0;
        int c = 0;
        int rowPos = 0;
        int rowSize = 1;          // sw=1 w set_ag_params z EncodeStereo (numSamples, numSamples)
        int rowJump = 0;          // fw - sw = numSamples - numSamples = 0
        int inPtr = 0;

        while (c < numSamples)
        {
            int m = mb >> QBSHIFT;
            int k = Lg3a(m);
            if (k > kb)
                k = kb;
            m = (1 << k) - 1;

            int del = pc[inPtr++];
            rowPos++;
            int n = (AbsFunc(del) << 1) - ((del >> 31) & 1) - zmode;

            if (DynCode32(bitSize, (uint)m, (uint)k, (uint)n, out uint numBits, out uint value, out uint overflow, out uint overflowBits))
            {
                bits.Jam(bitPos, (int)numBits, value);
                bitPos += (int)numBits;
                bits.JamLarge(bitPos, (int)overflowBits, overflow);
                bitPos += (int)overflowBits;
            }
            else
            {
                bits.Jam(bitPos, (int)numBits, value);
                bitPos += (int)numBits;
            }

            c++;
            if (rowPos >= rowSize)
            {
                rowPos = 0;
                inPtr += rowJump;
            }

            mb = pb * (n + zmode) + mb - ((pb * mb) >> QBSHIFT);
            if (n > N_MAX_MEAN_CLAMP)
                mb = N_MEAN_CLAMP_VAL;
            zmode = 0;

            if (((mb << MMULSHIFT) < QB) && (c < numSamples))
            {
                zmode = 1;
                int nz = 0;
                while (c < numSamples && pc[inPtr] == 0)
                {
                    inPtr++;
                    nz++;
                    c++;
                    if (++rowPos >= rowSize)
                    {
                        rowPos = 0;
                        inPtr += rowJump;
                    }
                    if (nz >= 65535)
                    {
                        zmode = 0;
                        break;
                    }
                }

                k = Lead(mb) - BITOFF + ((mb + MOFF) >> MDENSHIFT);
                int mz = ((1 << k) - 1) & wb;
                value = (uint)DynCode(mz, k, nz, out numBits);
                bits.Jam(bitPos, (int)numBits, value);
                bitPos += (int)numBits;
                mb = 0;
            }
        }

        outNumBits = (uint)(bitPos - startPos);
        bits.Advance((int)outNumBits);
    }

    private static int DynCode(int m, int k, int n, out uint outNumBits)
    {
        uint div = (uint)n / (uint)m;
        uint numBits, value;
        if (div >= MAX_PREFIX_16)
        {
            numBits = (uint)(MAX_PREFIX_16 + MAX_DATATYPE_BITS_16);
            value = (((1 << MAX_PREFIX_16) - 1) << MAX_DATATYPE_BITS_16) + (uint)n;
        }
        else
        {
            uint mod = (uint)n % (uint)m;
            uint de = (mod == 0) ? 1u : 0u;
            numBits = div + (uint)k + 1 - de;
            value = (((1u << (int)div) - 1) << (int)(numBits - div)) + mod + 1 - de;
            if (numBits > MAX_PREFIX_16 + MAX_DATATYPE_BITS_16)
            {
                numBits = (uint)(MAX_PREFIX_16 + MAX_DATATYPE_BITS_16);
                value = (((1 << MAX_PREFIX_16) - 1) << MAX_DATATYPE_BITS_16) + (uint)n;
            }
        }
        outNumBits = numBits;
        return (int)value;
    }

    private static bool DynCode32(int maxbits, uint m, uint k, uint n, out uint outNumBits, out uint outValue, out uint overflow, out uint overflowBits)
    {
        uint div = n / m;
        uint numBits, value;

        if (div < MAX_PREFIX_32)
        {
            uint mod = n - (m * div);
            uint de = (mod == 0) ? 1u : 0u;
            numBits = div + k + 1 - de;
            value = (((1u << (int)div) - 1) << (int)(numBits - div)) + mod + 1 - de;
            if (numBits <= 25)
            {
                outNumBits = numBits;
                outValue = value;
                overflow = 0;
                overflowBits = 0;
                return false;
            }
        }

        // escape
        numBits = MAX_PREFIX_32;
        value = (uint)((1 << MAX_PREFIX_32) - 1);
        outNumBits = numBits;
        outValue = value;
        overflow = n;
        overflowBits = (uint)maxbits;
        return true;
    }

    // =====================================================================
    // Helpery (lead, lg3a, abs, sign) — port ag_enc.c / dp_enc.c
    // =====================================================================
    private static int Lead(int m)
    {
        uint c = 1u << 31;
        int j;
        for (j = 0; j < 32; j++)
        {
            if ((c & (uint)m) != 0)
                break;
            c >>= 1;
        }
        return j;
    }

    private static int Lg3a(int x)
    {
        x += 3;
        return 31 - Lead(x);
    }

    private static int AbsFunc(int a)
    {
        int isneg = a >> 31;
        int xorval = a ^ isneg;
        return xorval - isneg;
    }

    private static int SignOfInt(int i)
    {
        uint negishift = (uint)(-i) >> 31;
        return (int)negishift | (i >> 31);
    }

    private static short[] GetCoefs(short[][][] coefs, int channel, int search) =>
        coefs[channel][search];

    // =====================================================================
    // BitWriter — port BitBuffer (write-only) z ALACBitUtilities.c
    // =====================================================================
    private sealed class BitWriter
    {
        public byte[] Buffer { get; }
        private readonly int _byteSize;
        private int _bytePos;
        private int _bitIndex;

        public BitWriter(Span<byte> buffer, int byteSize)
        {
            Buffer = new byte[byteSize];
            _byteSize = byteSize;
            buffer[..byteSize].CopyTo(Buffer);
            _bytePos = 0;
            _bitIndex = 0;
        }

        public int PositionBits => _bytePos * 8 + _bitIndex;
        public int PositionBytes => (_bytePos * 8 + _bitIndex) / 8;

        public void Reset()
        {
            _bytePos = 0;
            _bitIndex = 0;
        }

        public void Write(uint value, int numBits)
        {
            int invBitIndex = 8 - _bitIndex;
            while (numBits > 0)
            {
                int curNum = Math.Min(invBitIndex, numBits);
                uint tmp = value >> (numBits - curNum);
                int shift = invBitIndex - curNum;
                byte mask = (byte)(0xff >> (8 - curNum));
                mask <<= shift;
                Buffer[_bytePos] = (byte)((Buffer[_bytePos] & ~mask) | (((byte)tmp << shift) & mask));
                numBits -= curNum;
                invBitIndex -= curNum;
                if (invBitIndex == 0)
                {
                    invBitIndex = 8;
                    _bytePos++;
                }
            }
            _bitIndex = 8 - invBitIndex;
        }

        public void ByteAlign(bool addZeros)
        {
            if (_bitIndex == 0)
                return;
            if (addZeros)
                Write(0, 8 - _bitIndex);
            else
                Advance(8 - _bitIndex);
        }

        public void Advance(int numBits)
        {
            if (numBits != 0)
            {
                _bitIndex += numBits;
                _bytePos += _bitIndex >> 3;
                _bitIndex &= 7;
            }
        }

 /// <summary>dyn_jam_noDeref — zapis numBits values na pozycji bitPos (absolutnej od startu bufora).</summary>
        public void Jam(int bitPos, int numBits, uint value)
        {
            int byteOffset = bitPos >> 3;
            int shift = 32 - (bitPos & 7) - numBits;
            uint mask = ~0u >> (32 - numBits);
            mask <<= shift;

            uint curr = ReadUInt32BE(byteOffset);
            value = (value << shift) & mask;
            value |= curr & ~mask;
            WriteUInt32BE(byteOffset, value);
        }

 /// <summary>dyn_jam_noDeref_large — zapis crossing boundary 4 bytes.</summary>
        public void JamLarge(int bitPos, int numBits, uint value)
        {
            int byteOffset = bitPos >> 3;
            int shiftvalue = 32 - (bitPos & 7) - numBits;

            uint curr = ReadUInt32BE(byteOffset);
            uint w;
            if (shiftvalue < 0)
            {
                w = value >> -shiftvalue;
                uint mask = ~0u >> -shiftvalue;
                w |= curr & ~mask;
                WriteUInt32BE(byteOffset, w);
                byte tailbyte = (byte)((value << (8 + shiftvalue)) & 0xff);
                Buffer[byteOffset + 4] = tailbyte;
            }
            else
            {
                uint mask = ~0u >> (32 - numBits);
                mask <<= shiftvalue;
                w = (value << shiftvalue) & mask;
                w |= curr & ~mask;
                WriteUInt32BE(byteOffset, w);
            }
        }

        private uint ReadUInt32BE(int byteOffset)
        {
            if (byteOffset + 3 >= Buffer.Length)
                return 0;
            return ((uint)Buffer[byteOffset] << 24) |
                   ((uint)Buffer[byteOffset + 1] << 16) |
                   ((uint)Buffer[byteOffset + 2] << 8) |
                   Buffer[byteOffset + 3];
        }

        private void WriteUInt32BE(int byteOffset, uint value)
        {
            if (byteOffset + 3 >= Buffer.Length)
                return;
            Buffer[byteOffset] = (byte)(value >> 24);
            Buffer[byteOffset + 1] = (byte)(value >> 16);
            Buffer[byteOffset + 2] = (byte)(value >> 8);
            Buffer[byteOffset + 3] = (byte)value;
        }
    }
}
