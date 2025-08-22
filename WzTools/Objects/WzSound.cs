﻿﻿using System;
  using System.Buffers.Binary;
  using System.Collections.Generic;
  using System.Collections.Immutable;
  using System.Diagnostics;
  using System.IO;
  using System.Linq;
  using System.Runtime.InteropServices;
  using WzTools.Helpers;

  namespace WzTools.Objects
{
    public class WzSound : PcomObject
    {
        public override string SerializedName => "Sound_DX8";
        public byte[] Blob = null;

        // Wave header may be encrypted and there doesn't seem to be a way to retrieve this value from ArchiveReader
        private static readonly byte[] WzKey =
        [
            0xAB, 0x65, 0x49, 0x05, 0x67, 0xCD, 0x57, 0x0A, 0x98, 0x7B, 0x87, 0x0A, 0xEC, 0x65, 0x07, 0x8B
        ];
        
        private const bool VerboseDebug = false;
        
        #region Audio types and structs

        private static readonly Guid MEDIATYPE_Stream = new Guid("{E436EB83-524F-11CE-9F53-0020AF0BA770}");
        private static readonly Guid MEDIASUBTYPE_WAVE = new Guid("{E436EB8B-524F-11CE-9F53-0020AF0BA770}");
        private static readonly Guid WMFORMAT_WaveFormatEx = new Guid("{05589F81-C356-11CE-BF01-00AA0055595A}");
        private const int FormatTag_MP3 = 0x0055;
        private const int FormatTag_PCM = 0x0001;
        private const int CodecDelay_Fraunhofer = 1393;
        private const int CodecDelay_LAME = 0;
        private const int MpegLayer3_ID_MPEG = 1;
        private const int MpegLayer3_Flag_Padding_Off = 2;
        
        // http://soundfile.sapp.org/doc/WaveFormat/
        private static readonly byte[] WavRiffChunkId = "RIFF"u8.ToArray();
        private static readonly byte[] WavRiffFormat = "WAVE"u8.ToArray();
        private static readonly byte[] WavRiffSubchunk1Id = "fmt "u8.ToArray();
        private static readonly uint WavSubchunk1Size = 0x00000010; // PCM expects 0x10
        private static readonly byte[] WavSubchunk2Id = "data"u8.ToArray();

        public enum WzAudioFormat
        {
            Blob = 0,
            Mp3 = 1,
            Wav = 2,
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct WaveFormatEx
        {
            public short FormatTag;
            public short Channels;
            public int SamplesPerSec;
            public int AvgBytesPerSec;
            public short BlockAlign;
            public short BitsPerSample; // 8 bits = 8, 16 bits = 16, etc.
            public short Size;

            public override string ToString()
            {
                return $"WAVEFORMATEX - wFormatTag: 0x{FormatTag:X4}, nChannels: 0x{Channels:X4}, nSamplesPerSec: " +
                       $"{SamplesPerSec}, nAvgBytesPerSec: {AvgBytesPerSec}, nBlockAlign: 0x{BlockAlign:X4}, " +
                       $"wBitsPerSample: 0x{BitsPerSample:X4}, cbSize: 0x{Size:X4}";
            }
        }
        
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct MpegLayer3WaveFormat
        {
            public WaveFormatEx BaseWaveFormat;
            
            public short ID;
            public int Flags;
            public short BlockSize; // roundtrip fail
            public short FramesPerBlock;
            public short CodecDelay; // not a lot of docs on this; LAME = 0, Fraunhofer = 1393? rsvp contains both
            public override string ToString()
            {
                return $"{BaseWaveFormat}\nMPEGLAYER3WAVEFORMAT - wID: {ID}, fdwFlags: {Flags}, " +
                       $"nBlockSize: {BlockSize}, nFramesPerBlock: {FramesPerBlock}, nCodecDelay: {CodecDelay}";
            }
        }
        
        #endregion
        
        public override void Read(ArchiveReader reader)
        {
            Blob = reader.ReadBytes(BlobSize);
        }

        public override void Write(ArchiveWriter writer)
        {
            writer.Write(Blob);
        }

        public override void Set(string key, object value)
        {
            return;
        }

        public override object Get(string key)
        {
            return null;
        }

        public override ICollection<object> Children { get; } = ImmutableList<object>.Empty;

        public override bool HasChild(string key)
        {
            throw new NotImplementedException();
        }

        public override void Dispose()
        {
            Blob = null;
        }

        private static void ApplyWzKey(byte[] buffer)
        {
            for (int i = 0; i < WzKey.Length; i++) 
                buffer[i] ^= WzKey[i];
        }

        /// <summary>
        /// Sound header, prior to WAVEFORMAT+length
        /// </summary>
        internal class WzSoundHeader
        {
            internal byte UnkMaybeVariant = 0; // usually 0
            internal int DataLength = 0; // payload specific
            internal int AudioDuration = 0; // payload specific, mp3 fails exact roundtrip but is close enough
            internal byte UnkMaybeFormat = 2; // usually 2 (even for mono audio)

            // usually MEDIATYPE_Stream {E436EB83-524F-11CE-9F53-0020AF0BA770} 
            internal Guid MediaMajorType = MEDIATYPE_Stream;
            
            // usually MEDIASUBTYPE_WAVE {E436EB8B-524F-11CE-9F53-0020AF0BA770}
            internal Guid MediaSubType = MEDIASUBTYPE_WAVE;
            
            internal bool UnkMaybeFixedSampleSize = false;  // usually 0
            internal bool UnkMaybeTemporalCompression = true; // usually 1
            
            // usually WMFORMAT_WaveFormatEx {05589F81-C356-11CE-BF01-00AA0055595A}
            internal Guid MediaFormatType = WMFORMAT_WaveFormatEx;

            internal byte[] FormatStructBytes = [];
            internal byte[] SoundData = [];
            internal bool HeaderEncryptionEnabled = false;
            
            public override string ToString()
            {
                return $"SoundHeader: Major {MediaMajorType} Minor {MediaSubType} Format {MediaFormatType} " +
                       $"Var? {UnkMaybeVariant} Fmt? {UnkMaybeFormat} FSS? {UnkMaybeFixedSampleSize} " +
                       $"TC?? {UnkMaybeTemporalCompression}, " +
                       $"DataLength: {DataLength} AudioDuration: {AudioDuration}";
            }

            public static WzSoundHeader FromArchiveReader(ArchiveReader soundReader, short formatTag)
            {
                // appears to be inspired by AM_MEDIA_TYPE
                // https://learn.microsoft.com/en-us/windows/win32/api/strmif/ns-strmif-am_media_type
                WzSoundHeader soundHeader = new WzSoundHeader();
                soundHeader.UnkMaybeVariant = soundReader.ReadByte();
                soundHeader.DataLength = soundReader.ReadCompressedInt();
                soundHeader.AudioDuration = soundReader.ReadCompressedInt();
                soundHeader.UnkMaybeFormat = soundReader.ReadByte();
                soundHeader.MediaMajorType  = new Guid(soundReader.ReadBytes(16));
                soundHeader.MediaSubType = new Guid(soundReader.ReadBytes(16));
                soundHeader.UnkMaybeFixedSampleSize = soundReader.ReadBoolean();
                soundHeader.UnkMaybeTemporalCompression = soundReader.ReadBoolean();
                soundHeader.MediaFormatType = new Guid(soundReader.ReadBytes(16));
                
                soundHeader.Validate();
                
                // the following struct should at least be a WaveFormatEx-shaped object
                byte formatStructSize = soundReader.ReadByte();
                if (formatStructSize < Marshal.SizeOf<WaveFormatEx>())
                    throw new NotImplementedException($"formatStructSize will not fit a WaveFormatEx");
            
                soundHeader.FormatStructBytes = soundReader.ReadBytes(formatStructSize);
                WaveFormatEx waveHeader = BytesToStruct<WaveFormatEx>(soundHeader.FormatStructBytes);
            
                // Probe format tag, if unexpected, decrypt and retry. Present in some rsvp sounds
                if (waveHeader.FormatTag != formatTag)
                    ApplyWzKey(soundHeader.FormatStructBytes);
                
                waveHeader = BytesToStruct<WaveFormatEx>(soundHeader.FormatStructBytes);
            
                // No bueno
                if (waveHeader.FormatTag != formatTag)
                    throw new NotImplementedException($"Unexpected FormatTag ({waveHeader.FormatTag:X4})" +
                                                      $" expecting ({formatTag}:X4)");
                
                soundHeader.SoundData = soundReader.ReadBytes(soundHeader.DataLength);
                return soundHeader;
            }

            /// <summary>
            /// Tests fields in WzSoundHeader and throws an exception if any value deviates from expected MP3 values 
            /// </summary>
            /// <exception cref="NotImplementedException"></exception>
            private void Validate()
            {
                var refHeader = new WzSoundHeader();
                
                if (UnkMaybeVariant != refHeader.UnkMaybeVariant)
                    throw new NotImplementedException($"{nameof(UnkMaybeVariant)}: unexpected value " +
                                                      $"{UnkMaybeVariant}, expects {refHeader.UnkMaybeVariant}");
                if (UnkMaybeFormat != refHeader.UnkMaybeFormat)
                    throw new NotImplementedException($"{nameof(UnkMaybeFormat)}: unexpected value " +
                                                      $"{UnkMaybeFormat}, expects {refHeader.UnkMaybeFormat}");
                if (UnkMaybeFixedSampleSize != refHeader.UnkMaybeFixedSampleSize)
                    throw new NotImplementedException($"{nameof(UnkMaybeFixedSampleSize)}: unexpected value " +
                                                      $"{UnkMaybeFixedSampleSize}, expects " +
                                                      $"{refHeader.UnkMaybeFixedSampleSize}");
                if (UnkMaybeTemporalCompression != refHeader.UnkMaybeTemporalCompression)
                    throw new NotImplementedException($"{nameof(UnkMaybeTemporalCompression)}: unexpected value " +
                                                      $"{UnkMaybeTemporalCompression}, expects " +
                                                      $"{refHeader.UnkMaybeTemporalCompression}");
                
                if (MediaMajorType != refHeader.MediaMajorType)
                    throw new NotImplementedException($"{nameof(MediaMajorType)}: unexpected value " +
                                                      $"{MediaMajorType}, expects {refHeader.MediaMajorType}");
                if (MediaSubType != refHeader.MediaSubType)
                    throw new NotImplementedException($"{nameof(MediaSubType)}: unexpected value " +
                                                      $"{MediaSubType}, expects {refHeader.MediaSubType}");
                if (MediaFormatType != refHeader.MediaFormatType)
                    throw new NotImplementedException($"{nameof(MediaFormatType)}: unexpected value " +
                                                      $"{MediaFormatType}, expects {refHeader.MediaFormatType}");
            }
        }

        public WzAudioFormat GetAudio(out byte[] data)
        {
            if (TryGetMp3(out byte[] mp3))
            {
                data = mp3;
                return WzAudioFormat.Mp3;
            }
            if (TryGetPcm(out byte[] pcm))
            {
                data = pcm;
                return WzAudioFormat.Wav;
            }
            data = Blob;
            // as of rsvp-data/6253271a5148eedab0bc983db32be6818bead393, there should not be any fallback blobs
            return WzAudioFormat.Blob;
        }
        
        private bool TryGetMp3(out byte[] mp3)
        {
            try
            {
                mp3 = GetMp3();
                return true;
            }
            catch
            {
                mp3 = null;
                return false;
            }
        }
        
        private bool TryGetPcm(out byte[] pcm)
        {
            try
            {
                pcm = GetPcm();
                return true;
            }
            catch
            {
                pcm = null;
                return false;
            }
        }

        private byte[] GetPcm()
        {
            ArchiveReader soundReader = new ArchiveReader(new MemoryStream(Blob));
            WzSoundHeader soundHeader = WzSoundHeader.FromArchiveReader(soundReader, FormatTag_PCM);
            WaveFormatEx waveHeader = BytesToStruct<WaveFormatEx>(soundHeader.FormatStructBytes);
            
            Debug.WriteLineIf(VerboseDebug, soundHeader);
            Debug.WriteLineIf(VerboseDebug, waveHeader);
            
            // Tack on a RIFF header
            int waveSubFilesize = 36 + soundHeader.SoundData.Length;
            int waveActualFileSize = waveSubFilesize + 8;
            
            ArchiveWriter writer = new ArchiveWriter(new MemoryStream(waveActualFileSize));
            writer.Write(WavRiffChunkId);
            writer.Write(waveSubFilesize);
            writer.Write(WavRiffFormat);
            writer.Write(WavRiffSubchunk1Id);
            writer.Write(WavSubchunk1Size);
            writer.Write(waveHeader.FormatTag);
            writer.Write(waveHeader.Channels);
            writer.Write(waveHeader.SamplesPerSec);
            writer.Write(waveHeader.AvgBytesPerSec);
            writer.Write(waveHeader.BlockAlign);
            writer.Write(waveHeader.BitsPerSample);
            writer.Write(WavSubchunk2Id);
            writer.Write(soundHeader.SoundData.Length);
            writer.Write(soundHeader.SoundData);

            writer.Flush();
            return ((MemoryStream)writer.BaseStream).ToArray();
        }
        private byte[] GetMp3()
        {
            ArchiveReader soundReader = new ArchiveReader(new MemoryStream(Blob));
            WzSoundHeader soundHeader = WzSoundHeader.FromArchiveReader(soundReader, FormatTag_MP3);
            
            if (soundHeader.FormatStructBytes.Length < Marshal.SizeOf<MpegLayer3WaveFormat>())
                throw new NotImplementedException($"formatStructSize will not fit a MpegLayer3WaveFormat");
            MpegLayer3WaveFormat waveHeader = BytesToStruct<MpegLayer3WaveFormat>(soundHeader.FormatStructBytes);
            
            Debug.WriteLineIf(VerboseDebug, soundHeader);
            Debug.WriteLineIf(VerboseDebug, waveHeader);
            return soundHeader.SoundData;
        }

        public void SetAudio(byte[] audio, WzAudioFormat format, bool encrypt = false)
        {
            if (format == WzAudioFormat.Blob)
                Blob = audio;
            else if (format == WzAudioFormat.Wav)
                SetPcmWav(audio, encrypt);
            else if (format == WzAudioFormat.Mp3)
                SetMp3(audio, encrypt);
        }
        
        private void SetMp3(byte[] mp3, bool encrypt = false)
        {
            // Serialization roundtrip: get title MP3, replace via set, serialize obj and compare
            // Tested on Sound/BgmUI.img/Title
            // So far the 3 differences that fail the roundtrip are ...
            // Duration: not 100% but very close due to MP3's frame behavior. Our values are exact with Audacity
            // BlockSize: doesn't match maple's. Doesn't affect maple playback?
            // CodecDelay: this detail isn't embedded in most maple MP3s, in rsvp, both LAME/Fraunhofer are used
            
            int cursor = 0;
            int framesParsed = 0;
            int sumBitrate = 0;
            int sumSamplesPerSecond = 0;
            int blockAlign = 0;
            bool isStereo = false;
            int frameSize = 0;
            double sumPeriod = 0;
            do
            {
                var remaining = new Span<byte>(mp3, cursor, mp3.Length - cursor);
                if (MpegHeader.TryParseFrame(remaining, out MpegHeader mpegHeader))
                {
                    cursor += mpegHeader.GetFrameSize();
                    framesParsed++;
                    sumBitrate += mpegHeader.GetBitrate();
                    sumPeriod += mpegHeader.GetFramePeriod();
                    sumSamplesPerSecond += mpegHeader.GetSampleRate();
                    blockAlign = mpegHeader.GetSlotSizeBytes();
                    isStereo = mpegHeader.IsStereo();
                    frameSize = mpegHeader.GetFrameSize();
                }
                else
                {
                    cursor++;
                }
            }
            while (cursor < mp3.Length);
            
            // Walked through all frames so VBR should hopefully work fine
            int averageByteRate = sumBitrate / framesParsed / 8;
            int averageSamplesPerSecond = sumSamplesPerSecond / framesParsed;
            int durationMs = (int)(sumPeriod * 1000);
            Debug.WriteLineIf(VerboseDebug, $"Write post parse, avg br {averageByteRate}, " +
                                            $"duration {durationMs}, stereo {isStereo}, align: {blockAlign}, " +
                                            $"sps {averageSamplesPerSecond}, total frames: {framesParsed}, " +
                                            $"datalength: {mp3.Length}");

            // Build MP3 header from earlier values
            MpegLayer3WaveFormat waveHeader = new MpegLayer3WaveFormat();
            waveHeader.BaseWaveFormat.FormatTag = FormatTag_MP3;
            waveHeader.BaseWaveFormat.Channels = (short)(isStereo ? 2 : 1);
            waveHeader.BaseWaveFormat.SamplesPerSec = averageSamplesPerSecond;
            waveHeader.BaseWaveFormat.AvgBytesPerSec = averageByteRate;
            waveHeader.BaseWaveFormat.BlockAlign = (short)blockAlign;
            waveHeader.BaseWaveFormat.BitsPerSample = 0; // no idea, always zero
            waveHeader.BaseWaveFormat.Size = (short)(Marshal.SizeOf<MpegLayer3WaveFormat>() 
                                                     - Marshal.SizeOf<WaveFormatEx>()); // expects 0x000C

            waveHeader.ID = MpegLayer3_ID_MPEG;
            waveHeader.Flags = MpegLayer3_Flag_Padding_Off; // no idea either, maple default
            waveHeader.BlockSize = (short)frameSize; // fails RT for bgm title (why?). mp3 still works, field optional?
            waveHeader.FramesPerBlock = 1; // huh? maple default
            waveHeader.CodecDelay = CodecDelay_LAME; // "newer" encoders use LAME
            
            // Build wz-specific header
            WzSoundHeader soundHeader = new();
            soundHeader.DataLength = mp3.Length;
            soundHeader.AudioDuration = durationMs;
            
            // Begin serializing
            ArchiveWriter writer = new ArchiveWriter(new MemoryStream());
            writer.Write(soundHeader.UnkMaybeVariant);
            writer.WriteCompressedInt(soundHeader.DataLength);
            writer.WriteCompressedInt(soundHeader.AudioDuration);
            writer.Write(soundHeader.UnkMaybeFormat);
            writer.Write(soundHeader.MediaMajorType.ToByteArray());
            writer.Write(soundHeader.MediaSubType.ToByteArray());
            writer.Write(soundHeader.UnkMaybeFixedSampleSize);
            writer.Write(soundHeader.UnkMaybeTemporalCompression);
            writer.Write(soundHeader.MediaFormatType.ToByteArray());

            byte[] waveHeaderBytes = StructToBytes(waveHeader);
            if (encrypt)
                ApplyWzKey(waveHeaderBytes);
            
            writer.Write((byte)Marshal.SizeOf<MpegLayer3WaveFormat>());
            writer.Write(waveHeaderBytes);
            writer.Write(mp3);
            
            writer.Flush();
            Blob = ((MemoryStream)writer.BaseStream).ToArray();
        }

        private void SetPcmWav(byte[] wav, bool encrypt = false)
        {
            // WAV roundtrip is perfect, discards encryption
            // Tested on Sound/Pet.img/5000013/happy
            ArchiveReader reader = new ArchiveReader(new MemoryStream(wav));
            byte[] riffChunkId = reader.ReadBytes(4);
            
            if (!riffChunkId.SequenceEqual(WavRiffChunkId))
                throw new NotImplementedException("Input does not appear to be a WAV file, missing RIFF header");
            
            reader.ReadInt32(); // subFilesize
            reader.ReadBytes(WavRiffFormat.Length + WavRiffSubchunk1Id.Length);
            reader.ReadInt32(); // subchunkSize
            
            // Build wave header
            WaveFormatEx waveHeader = new WaveFormatEx();
            waveHeader.FormatTag = reader.ReadInt16();
            waveHeader.Channels = reader.ReadInt16();
            waveHeader.SamplesPerSec = reader.ReadInt32();
            waveHeader.AvgBytesPerSec = reader.ReadInt32();
            waveHeader.BlockAlign = reader.ReadInt16();
            waveHeader.BitsPerSample = reader.ReadInt16();
            waveHeader.Size = 0;
            
            reader.ReadBytes(WavSubchunk2Id.Length);
            
            int dataLength = reader.ReadInt32();
            byte[] data = reader.ReadBytes(dataLength);
            
            // Build wz-specific header
            WzSoundHeader soundHeader = new();
            soundHeader.DataLength = dataLength;
            soundHeader.AudioDuration = (int)((double)dataLength / waveHeader.AvgBytesPerSec * 1000); // in ms
            
            // Begin serializing
            ArchiveWriter writer = new ArchiveWriter(new MemoryStream());
            writer.Write(soundHeader.UnkMaybeVariant);
            writer.WriteCompressedInt(soundHeader.DataLength);
            writer.WriteCompressedInt(soundHeader.AudioDuration);
            writer.Write(soundHeader.UnkMaybeFormat);
            writer.Write(soundHeader.MediaMajorType.ToByteArray());
            writer.Write(soundHeader.MediaSubType.ToByteArray());
            writer.Write(soundHeader.UnkMaybeFixedSampleSize);
            writer.Write(soundHeader.UnkMaybeTemporalCompression);
            writer.Write(soundHeader.MediaFormatType.ToByteArray());
            
            byte[] waveHeaderBytes = StructToBytes(waveHeader);
            if (encrypt)
                ApplyWzKey(waveHeaderBytes);
            
            writer.Write((byte)Marshal.SizeOf<WaveFormatEx>());
            writer.Write(waveHeaderBytes);
            writer.Write(data);
            
            writer.Flush();
            Blob = ((MemoryStream)writer.BaseStream).ToArray();
        }
        
        // Required for extracting mp3 metadata for sound header and waveformat (for writes)
        internal class MpegHeader
        {
            internal uint FrameHeader;
            internal bool ValidFrame;
            internal uint AudioVersionId;
            internal uint LayerIndex;
            internal bool ProtectionBitEnabled;
            internal uint BitrateIndex;
            internal uint SamplingRateIndex;
            internal bool PaddingEnabled;
            internal uint PrivateBit;
            internal uint ChannelMode;
            internal uint ModeExtension;
            internal uint CopyrightBit;
            internal uint OriginalBit;
            internal uint Emphasis;

            private static readonly int[,] BitRateTable =
            {
                // V2P5 R, V2P5 L3, V2P5 L2, V2P5 L1, R, R, R, R, V2 R, V2 L3, V2 L2, V2 L1, V1 R, V1 L3, V1 L2, V1 L1
                { -1, 0, 0, 0, -1, -1, -1, -1, -1, 0, 0, 0, -1, 0, 0, 0 },
                { -1, 0, 0, 0, -1, -1, -1, -1, -1, 8, 8, 32, -1, 32, 32, 32 },
                { -1, 0, 0, 0, -1, -1, -1, -1, -1, 16, 16, 48, -1, 40, 48, 64 },
                { -1, 0, 0, 0, -1, -1, -1, -1, -1, 24, 24, 56, -1, 48, 56, 96 },
                { -1, 0, 0, 0, -1, -1, -1, -1, -1, 32, 32, 64, -1, 56, 64, 128 },
                { -1, 0, 0, 0, -1, -1, -1, -1, -1, 40, 40, 80, -1, 64, 80, 160 },
                { -1, 0, 0, 0, -1, -1, -1, -1, -1, 48, 48, 96, -1, 80, 96, 192 },
                { -1, 0, 0, 0, -1, -1, -1, -1, -1, 56, 56, 112, -1, 96, 112, 224 },
                { -1, 0, 0, 0, -1, -1, -1, -1, -1, 64, 64, 128, -1, 112, 128, 256 },
                { -1, 0, 0, 0, -1, -1, -1, -1, -1, 80, 80, 144, -1, 128, 160, 288 },
                { -1, 0, 0, 0, -1, -1, -1, -1, -1, 96, 96, 160, -1, 160, 192, 320 },
                { -1, 0, 0, 0, -1, -1, -1, -1, -1, 112, 112, 176, -1, 192, 224, 352 },
                { -1, 0, 0, 0, -1, -1, -1, -1, -1, 128, 128, 192, -1, 224, 256, 384 },
                { -1, 0, 0, 0, -1, -1, -1, -1, -1, 144, 144, 224, -1, 256, 320, 416 },
                { -1, 0, 0, 0, -1, -1, -1, -1, -1, 160, 160, 256, -1, 320, 384, 448 },
                { -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1 },
            };
            
            private static readonly int[,] SampleRateTable =
            {
                // V2P5, R, V2, V1
                { 11025, 0, 22050, 44100 },
                { 12000, 0, 24000, 48000 },
                { 8000, 0, 16000, 32000 },
                { -1, 0, -1, -1 },
            };

            private static readonly int[,] SamplesPerFrameTable =
            {
                // V2P5, R, V2, V1
                { 0, 0, 0, 0 }, // R
                { 576, 0, 576, 1152 }, // L3
                { 1152, 0, 1152, 1152 }, // L2
                { 384, 0, 384, 384 }, // L1
            };
            public override string ToString()
            {
                return $"MpegHeader {FrameHeader:X8}. Valid? {ValidFrame}, V/L:{AudioVersionId}/{LayerIndex}, " +
                       $"BRI: {BitrateIndex}, SRI: {SamplingRateIndex}, Padding: {PaddingEnabled} " +
                       $"BR: {GetBitrate()}, SR: {GetSampleRate()}, SpF: {GetSamplesPerFrame()} " +
                       $"FS: {GetFrameSize()}(0x{GetFrameSize():X}) " +
                       $"FP: {GetFramePeriod()}";
            }

            internal int GetBitrate()
            {
                if (AudioVersionId < 2)
                    throw new NotImplementedException("Unable to handle MP3 that is neither MPEG1/MPEG2");
                
                int bitrate = BitRateTable[BitrateIndex, (AudioVersionId << 2) | LayerIndex];
                if (bitrate != -1)
                    return bitrate * 1000;
                return -1;
            }

            internal int GetSampleRate() => SampleRateTable[SamplingRateIndex, AudioVersionId];

            internal int GetSamplesPerFrame() => SamplesPerFrameTable[LayerIndex, AudioVersionId];

            internal int GetSlotSizeBytes() => LayerIndex == 0b11 ? 4 : 1; // 32 bits for layer 1

            // https://stackoverflow.com/questions/72416908/mp3-exact-frame-size-calculation
            internal int GetFrameSize() => GetSamplesPerFrame() / 8 * GetBitrate() / GetSampleRate() +
                                                 (PaddingEnabled ? 1 : 0) * GetSlotSizeBytes();

            internal double GetFramePeriod() => (double)GetSamplesPerFrame() / GetSampleRate();

            internal bool IsStereo() => ChannelMode != 0b11;

            internal static bool TryParseFrame(Span<byte> mp3, out MpegHeader header)
            {
                uint frameHeader = BinaryPrimitives.ReadUInt32BigEndian(mp3);
                header = new();
                int framePosition = 0;

                // https://www.codeproject.com/KB/audio-video/mpegaudioinfo.aspx#MPEGAudioFrame
                // https://checkmate.gissen.nl/headers.php
                header.FrameHeader = frameHeader;
                header.ValidFrame = ReadBits(11, frameHeader, ref framePosition) == CreateBitmask(11);
                if (!header.ValidFrame)
                    return false;
                header.AudioVersionId = ReadBits(2, frameHeader, ref framePosition);
                header.LayerIndex = ReadBits(2, frameHeader, ref framePosition);
                header.ProtectionBitEnabled = ReadBits(1, frameHeader, ref framePosition) == 0;
                header.BitrateIndex = ReadBits(4, frameHeader, ref framePosition);
                header.SamplingRateIndex = ReadBits(2, frameHeader, ref framePosition);
                header.PaddingEnabled = ReadBits(1, frameHeader, ref framePosition) == 1;
                header.PrivateBit = ReadBits(1, frameHeader, ref framePosition);
                header.ChannelMode = ReadBits(2, frameHeader, ref framePosition);
                header.ModeExtension = ReadBits(2, frameHeader, ref framePosition);
                header.CopyrightBit = ReadBits(1, frameHeader, ref framePosition);
                header.OriginalBit = ReadBits(1, frameHeader, ref framePosition);
                header.Emphasis = ReadBits(2, frameHeader, ref framePosition);
                return true;
            }
            private static uint CreateBitmask(int size)
            {
                return (uint)(1 << size) - 1;
            }

            private static uint ReadBits(int bitSize, uint frameHeader, ref int framePosition)
            {
                framePosition += bitSize;
                return (frameHeader >> (32 - framePosition)) & CreateBitmask(bitSize);
            }
        }

        private static byte[] StructToBytes<T>(T obj)
        {
            byte[] result = new byte[Marshal.SizeOf(obj)];
            GCHandle handle = GCHandle.Alloc(result, GCHandleType.Pinned);
            try
            {
                Marshal.StructureToPtr(obj, handle.AddrOfPinnedObject(), false);
                return result;
            }
            finally
            {
                handle.Free();
            }
        }

        private static T BytesToStruct<T>(byte[] data) where T : new()
        {
            GCHandle handle = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                return Marshal.PtrToStructure<T>(handle.AddrOfPinnedObject());
            }
            finally
            {
                handle.Free();
            }
        }

    }
}
