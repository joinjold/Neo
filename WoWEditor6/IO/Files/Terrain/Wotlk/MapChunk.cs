﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SharpDX;
using WoWEditor6.Editing;
using WoWEditor6.Scene;

namespace WoWEditor6.IO.Files.Terrain.Wotlk
{
    class MapChunk : Terrain.MapChunk
    {
        private readonly WeakReference<MapArea> mParent;
        private Mcnk mHeader;

        private readonly Vector4[] mShadingFloats = new Vector4[145];
        private bool mForceMccv;
        private byte[] mAlphaCompressed;
        private Mcly[] mLayers = new Mcly[0];
        private static readonly uint[] Indices = new uint[768];
        private readonly Dictionary<uint, DataChunk> mSaveChunks = new Dictionary<uint, DataChunk>();

        public override uint[] RenderIndices => Indices;

        public MapChunk(int indexX, int indexY, WeakReference<MapArea> parent)
        {
            IndexX = indexX;
            IndexY = indexY;
            mParent = parent;
            TextureScales = new float[] { 1, 1, 1, 1 };
            for (var i = 0; i < 145; ++i) mShadingFloats[i] = Vector4.One;
        }

        public void SaveChunk(BinaryWriter writer)
        {
            var basePos = (int) writer.BaseStream.Position;
            writer.Write(0x4D434E4B);
            writer.Write(0);
            var header = mHeader;
            var headerPos = writer.BaseStream.Position;
            var startPos = writer.BaseStream.Position;
            writer.Write(header);

            SaveHeights(writer, basePos, ref header);
            SaveNormals(writer, basePos, ref header);
            SaveMccv(writer, basePos, ref header);
            // INFO: SaveAlpha must be called before SaveLayers since SaveAlpha modifies the layer flags
            SaveAlpha(writer, basePos, ref header);
            SaveLayers(writer, basePos, ref header);
            SaveUnusedChunks(writer, basePos, ref header);

            var endPos = writer.BaseStream.Position;
            writer.BaseStream.Position = headerPos;
            writer.Write(header);
            writer.BaseStream.Position = headerPos - 4;
            writer.Write((int) (endPos - startPos));
            writer.BaseStream.Position = endPos;
        }

        public bool AsyncLoad(BinaryReader reader, ChunkInfo chunkInfo)
        {
            // chunkInfo.Offset points to right after the MCNK signature, the offsets in the header are relative to the signature tho
            var basePosition = chunkInfo.Offset - 4;
            reader.BaseStream.Position = chunkInfo.Offset;
            reader.ReadInt32();
            mHeader = reader.Read<Mcnk>();
            reader.BaseStream.Position = basePosition + mHeader.Mcvt;
            var signature = reader.ReadUInt32();
            reader.ReadInt32();
            if (signature != 0x4D435654)
            {
                Log.Error("Chunk is missing valid MCVT sub chunk");
                return false;
            }

            LoadMcvt(reader);

            reader.BaseStream.Position = basePosition + mHeader.Mcnr;
            signature = reader.ReadUInt32();
            reader.ReadInt32();

            if (signature != 0x4D434E52)
            {
                Log.Error("Chunk is missing valid MCNR sub chunk");
                return false;
            }

            LoadMcnr(reader);

            var hasMccv = false;
            if (mHeader.Mccv != 0)
            {
                reader.BaseStream.Position = basePosition + mHeader.Mccv;
                signature = reader.ReadUInt32();
                reader.ReadInt32();
                if (signature == 0x4D434356)
                {
                    LoadMccv(reader);
                    hasMccv = true;
                    mForceMccv = true;
                }
            }

            reader.BaseStream.Position = basePosition + mHeader.Mcly;
            signature = reader.ReadUInt32();
            var size = reader.ReadInt32();

            if (signature != 0x4D434C59)
                return false;

            LoadLayers(reader, size);

            if (mHeader.SizeAlpha > 8)
            {
                reader.BaseStream.Position = basePosition + mHeader.Mcal;
                signature = reader.ReadUInt32();
                if (signature == 0x4D43414C)
                {
                    reader.ReadInt32();
                    mAlphaCompressed = reader.ReadBytes(mHeader.SizeAlpha - 8);
                }
            }

            if (hasMccv == false)
            {
                for (var i = 0; i < 145; ++i)
                    Vertices[i].Color = 0x7F7F7F7F;
            }

            LoadUnusedChunk(0x4D435246, basePosition + mHeader.Mcrf, 0, reader);
            if (mHeader.SizeShadow > 0)
                LoadUnusedChunk(0x4D435348, basePosition + mHeader.Mcsh, mHeader.SizeShadow, reader);
            if (mHeader.NumSoundEmitters > 0)
                LoadUnusedChunk(0x4D435345, basePosition + mHeader.Mcse, mHeader.NumSoundEmitters * 0x1C, reader);
            if (mHeader.SizeLiquid > 8)
                LoadUnusedChunk(0x4D434C51, basePosition + mHeader.Mclq, mHeader.SizeLiquid - 8, reader);

            LoadUnusedChunk(0x4D434C56, basePosition + mHeader.Mclv, 0, reader);

            InitLayerData();

            WorldFrame.Instance.MapManager.OnLoadProgress();

            return true;
        }

        public override void UpdateNormals()
        {
            if (mUpdateNormals == false)
                return;

            mUpdateNormals = false;
            for (var i = 0; i < 145; ++i)
            {
                var p1 = Vertices[i].Position;
                var p2 = p1;
                var p3 = p2;
                var p4 = p3;
                var v = p1;

                p1.X -= 0.5f * Metrics.UnitSize;
                p1.Y -= 0.5f * Metrics.UnitSize;
                p2.X += 0.5f * Metrics.UnitSize;
                p2.Y -= 0.5f * Metrics.UnitSize;
                p3.X += 0.5f * Metrics.UnitSize;
                p3.Y += 0.5f * Metrics.UnitSize;
                p4.X -= 0.5f * Metrics.UnitSize;
                p4.Y += 0.5f * Metrics.UnitSize;

                var mgr = WorldFrame.Instance.MapManager;
                float h;
                if (mgr.GetLandHeight(p1.X, p1.Y, out h)) p1.Z = h;
                if (mgr.GetLandHeight(p2.X, p2.Y, out h)) p2.Z = h;
                if (mgr.GetLandHeight(p3.X, p3.Y, out h)) p3.Z = h;
                if (mgr.GetLandHeight(p4.X, p4.Y, out h)) p4.Z = h;

                var n1 = Vector3.Cross((p2 - v), (p1 - v));
                var n2 = Vector3.Cross((p3 - v), (p2 - v));
                var n3 = Vector3.Cross((p4 - v), (p3 - v));
                var n4 = Vector3.Cross((p1 - v), (p4 - v));

                var n = n1 + n2 + n3 + n4;
                n.Normalize();
                n.Z *= -1;

                n.X = ((sbyte)(n.X * 127)) / 127.0f;
                n.Y = ((sbyte)(n.Y * 127)) / 127.0f;
                n.Z = ((sbyte)(n.Z * 127)) / 127.0f;

                Vertices[i].Normal = n;
            }

            MapArea parent;
            mParent.TryGetTarget(out parent);
            parent?.UpdateVertices(this);
        }

        public override bool OnTerrainChange(TerrainChangeParameters parameters)
        {
            var changed = base.OnTerrainChange(parameters);

            if (changed)
            {
                MapArea parent;
                mParent.TryGetTarget(out parent);

                var omin = BoundingBox.Minimum;
                var omax = BoundingBox.Maximum;
                BoundingBox = new BoundingBox(new Vector3(omin.X, omin.Y, mMinHeight),
                new Vector3(omax.X, omax.Y, mMaxHeight));

                parent?.UpdateBoundingBox(BoundingBox);
            }

            return changed;
        }

        public bool Intersect(ref Ray ray, out float distance)
        {
            distance = float.MaxValue;
            if (BoundingBox.Intersects(ref ray) == false)
                return false;

            var minDist = float.MaxValue;
            var hasHit = false;
            var dir = ray.Direction;
            var orig = ray.Position;

            Vector3 e1, e2, p, T, q;

            for (var i = 0; i < Indices.Length; i += 3)
            {
                var i0 = Indices[i];
                var i1 = Indices[i + 1];
                var i2 = Indices[i + 2];
                Vector3.Subtract(ref Vertices[i1].Position, ref Vertices[i0].Position, out e1);
                Vector3.Subtract(ref Vertices[i2].Position, ref Vertices[i0].Position, out e2);

                Vector3.Cross(ref dir, ref e2, out p);
                float det;
                Vector3.Dot(ref e1, ref p, out det);

                if (Math.Abs(det) < 1e-4)
                    continue;

                var invDet = 1.0f / det;
                Vector3.Subtract(ref orig, ref Vertices[i0].Position, out T);
                float u;
                Vector3.Dot(ref T, ref p, out u);
                u *= invDet;

                if (u < 0 || u > 1)
                    continue;

                Vector3.Cross(ref T, ref e1, out q);
                float v;
                Vector3.Dot(ref dir, ref q, out v);
                v *= invDet;
                if (v < 0 || (u + v) > 1)
                    continue;

                float t;
                Vector3.Dot(ref e2, ref q, out t);
                t *= invDet;

                if (t < 1e-4) continue;

                hasHit = true;
                if (t < minDist)
                    minDist = t;
            }

            if (hasHit)
                distance = minDist;

            return hasHit;
        }

        public override void Dispose()
        {

        }

        protected override bool HandleMccvPaint(TerrainChangeParameters parameters)
        {
            var amount = (parameters.Amount / 75.0f) * (float)parameters.TimeDiff.TotalSeconds;
            var changed = false;

            var destColor = parameters.Shading;
            if (parameters.Inverted)
            {
                destColor.X = 2 - destColor.X;
                destColor.Y = 2 - destColor.Y;
                destColor.Z = 2 - destColor.Z;
            }

            var radius = parameters.OuterRadius;
            for (var i = 0; i < 145; ++i)
            {
                var p = Vertices[i].Position;
                var dist = (p - parameters.Center).Length();
                if (dist > radius)
                    continue;

                mForceMccv = true;
                changed = true;
                var factor = dist / radius;
                if (dist < parameters.InnerRadius)
                    factor = 1.0f;

                var curColor = mShadingFloats[i];
                var dr = destColor.X - curColor.Z;
                var dg = destColor.Y - curColor.Y;
                var db = destColor.Z - curColor.X;

                var cr = Math.Min(Math.Abs(dr), amount * factor);
                var cg = Math.Min(Math.Abs(dg), amount * factor);
                var cb = Math.Min(Math.Abs(db), amount * factor);

                if (dr < 0)
                {
                    curColor.Z -= cr;
                    if (curColor.Z < destColor.X)
                        curColor.Z = destColor.X;
                }
                else
                {
                    curColor.Z += cr;
                    if (curColor.Z > destColor.X)
                        curColor.Z = destColor.X;
                }
                if (dg < 0)
                {
                    curColor.Y -= cg;
                    if (curColor.Y < destColor.Y)
                        curColor.Y = destColor.Y;
                }
                else
                {
                    curColor.Y += cg;
                    if (curColor.Y > destColor.Y)
                        curColor.Y = destColor.Y;
                }
                if (db < 0)
                {
                    curColor.X -= cb;
                    if (curColor.X < destColor.Z)
                        curColor.X = destColor.Z;
                }
                else
                {
                    curColor.X += cb;
                    if (curColor.X > destColor.Z)
                        curColor.X = destColor.Z;
                }

                mShadingFloats[i] = curColor;

                curColor.X = Math.Min(Math.Max(curColor.X, 0), 2);
                curColor.Y = Math.Min(Math.Max(curColor.Y, 0), 2);
                curColor.Z = Math.Min(Math.Max(curColor.Z, 0), 2);

                var r = (byte)((curColor.Z / 2.0f) * 255.0f);
                var g = (byte)((curColor.Y / 2.0f) * 255.0f);
                var b = (byte)((curColor.X / 2.0f) * 255.0f);
                var a = (byte)((curColor.W / 2.0f) * 255.0f);

                var color = (uint)((a << 24) | (r << 16) | (g << 8) | b);
                Vertices[i].Color = color;
            }

            return changed;
        }

        private void LoadUnusedChunk(uint signature, int offset, int size, BinaryReader reader)
        {
            if (offset == 0 || size == 0)
                return;

            reader.BaseStream.Position = offset;
            var sig = reader.ReadUInt32();
            if(sig != signature)
            {
                Log.Warning(
                    string.Format(
                        "Info: Expected signature {0:X8} inside chunk, got {1:X8}. Since this chunk is not used for rendering its ignored.",
                        signature, sig));
                return;
            }

            var dataSize = reader.ReadInt32();
            if(dataSize != size && size != 0)
            {
                Log.Warning(
                    string.Format(
                        "Info: Expected chunk size {0} was not the same as actual data size {1}. Chunk was: {2:X8}. Using expected chunk size.",
                        size, dataSize, signature));
            }

            var data = reader.ReadBytes(size);
            if (mSaveChunks.ContainsKey(signature))
                return;

            mSaveChunks.Add(signature, new DataChunk {Data = data, Signature = signature, Size = size});
        }

        private void LoadMcvt(BinaryReader reader)
        {
            var heights = reader.ReadArray<float>(145);

            var posx = Metrics.MapMidPoint - mHeader.Position.Y;
            var posy = Metrics.MapMidPoint - mHeader.Position.X;
            var posz = mHeader.Position.Z;

            var counter = 0;

            var minPos = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var maxPos = new Vector3(float.MinValue, float.MinValue, float.MinValue);

            for (var i = 0; i < 17; ++i)
            {
                for (var j = 0; j < (((i % 2) != 0) ? 8 : 9); ++j)
                {
                    var height = posz + heights[counter];
                    var x = posx + j * Metrics.UnitSize;
                    if ((i % 2) != 0)
                        x += 0.5f * Metrics.UnitSize;
                    var y = posy + i * Metrics.UnitSize * 0.5f;

                    Vertices[counter].Position = new Vector3(x, y, height);

                    if (height < minPos.Z)
                        minPos.Z = height;
                    if (height > maxPos.Z)
                        maxPos.Z = height;

                    if (x < minPos.X)
                        minPos.X = x;
                    if (x > maxPos.X)
                        maxPos.X = x;
                    if (y < minPos.Y)
                        minPos.Y = y;
                    if (y > maxPos.Y)
                        maxPos.Y = y;

                    Vertices[counter].TexCoordAlpha = new Vector2(j / 8.0f + ((i % 2) != 0 ? (0.5f / 8.0f) : 0), i / 16.0f);
                    Vertices[counter].TexCoord = new Vector2(j + ((i % 2) != 0 ? 0.5f : 0.0f), i * 0.5f);
                    ++counter;
                }
            }

            mMinHeight = minPos.Z;
            mMaxHeight = maxPos.Z;

            BoundingBox = new BoundingBox(minPos, maxPos);
            mMidPoint = minPos + (maxPos - minPos) / 2.0f;
        }

        private void LoadMcnr(BinaryReader reader)
        {
            var normals = reader.ReadArray<sbyte>(145 * 3);

            for(var i = 0; i < 145; ++i)
            {
                var nx = normals[i * 3] / -127.0f;
                var ny = normals[i * 3 + 1] / -127.0f;
                var nz = normals[i * 3 + 2] / 127.0f;

                Vertices[i].Normal = new Vector3(nx, ny, nz);
            }
        }

        private void LoadMccv(BinaryReader reader)
        {
            var colors = reader.ReadArray<uint>(145);
            for (var i = 0; i < 145; ++i)
            {
                Vertices[i].Color = colors[i];
                var r = (colors[i] >> 16) & 0xFF;
                var g = (colors[i] >> 8) & 0xFF;
                var b = (colors[i]) & 0xFF;
                var a = (colors[i] >> 24) & 0xFF;

                mShadingFloats[i] = new Vector4(b * 2.0f / 255.0f, g * 2.0f / 255.0f, r * 2.0f / 255.0f, a * 2.0f / 255.0f);
            }
        }

        private void LoadLayers(BinaryReader reader, int size)
        {
            mLayers = reader.ReadArray<Mcly>(size / SizeCache<Mcly>.Size);
            MapArea parent;
            if (mParent.TryGetTarget(out parent) == false)
            {
                Textures = new List<Graphics.Texture>();
                return;
            }

            Textures = mLayers.Select(l => parent.GetTexture(l.TextureId)).ToList().AsReadOnly();
        }

        private void InitLayerData()
        {
            var nLayers = Math.Min(mLayers.Length, 4);
            for (var i = 0; i < nLayers; ++i)
            {
                if ((mLayers[i].Flags & 0x200) != 0)
                    LoadLayerRle(mLayers[i], i);
                else if ((mLayers[i].Flags & 0x100) != 0)
                {
                    if (WorldFrame.Instance.MapManager.HasNewBlend)
                        LoadUncompressed(mLayers[i], i);
                    else
                        LoadLayerCompressed(mLayers[i], i);
                }
                else
                {
                    for (var j = 0; j < 4096; ++j)
                        AlphaValues[j] |= 0xFFu << (8 * i);
                }
            }
        }

        private void LoadUncompressed(Mcly layerInfo, int layer)
        {
            var startPos = layerInfo.OfsMcal;
            for (var i = 0; i < 4096; ++i)
                AlphaValues[i] |= (uint)mAlphaCompressed[startPos++] << (8 * layer);
        }

        private void LoadLayerCompressed(Mcly layerInfo, int layer)
        {
            var startPos = layerInfo.OfsMcal;
            var counter = 0;
            for (var k = 0; k < 63; ++k)
            {
                for (var j = 0; j < 32; ++j)
                {
                    var alpha = mAlphaCompressed[startPos++];
                    var val1 = alpha & 0xF;
                    var val2 = alpha >> 4;
                    val2 = j == 31 ? val1 : val2;
                    val1 = (byte)((val1 / 15.0f) * 255.0f);
                    val2 = (byte)((val2 / 15.0f) * 255.0f);
                    AlphaValues[counter++] |= (uint)val1 << (8 * layer);
                    AlphaValues[counter++] |= (uint)val2 << (8 * layer);
                }
            }

            for (uint j = 0; j < 64; ++j)
            {
                AlphaValues[63 * 64 + j] |= (uint)(AlphaValues[(62 * 64) + j] & (0xFF << (layer * 8)));
            }
        }

        private void LoadLayerRle(Mcly layerInfo, int layer)
        {
            var counterOut = 0;
            var startPos = layerInfo.OfsMcal;
            while (counterOut < 4096)
            {
                var indicator = mAlphaCompressed[startPos++];
                if ((indicator & 0x80) != 0)
                {
                    var value = mAlphaCompressed[startPos++];
                    var repeat = indicator & 0x7F;
                    for (var k = 0; k < repeat && counterOut < 4096; ++k)
                        AlphaValues[counterOut++] |= (uint)value << (layer * 8);
                }
                else
                {
                    for (var k = 0; k < (indicator & 0x7F) && counterOut < 4096; ++k)
                        AlphaValues[counterOut++] |= (uint)mAlphaCompressed[startPos++] << (8 * layer);
                }
            }
        }

        private void SaveHeights(BinaryWriter writer, int basePosition, ref Mcnk header)
        {
            header.Mcvt = (int) writer.BaseStream.Position - basePosition;
            var minPos = Vertices.Min(v => v.Position.Z);
            header.Position.Z = minPos;
            var heights = Vertices.Select(v => v.Position.Z - minPos);
            writer.Write(0x4D435654);
            writer.Write(145 * 4);
            writer.WriteArray(heights.ToArray());
        }

        private void SaveNormals(BinaryWriter writer, int basePosition, ref Mcnk header)
        {
            header.Mcnr = (int) writer.BaseStream.Position - basePosition;

            var normals =
                Vertices.SelectMany(v => new[] {(sbyte)(v.Normal.X * -127.0f), (sbyte)(v.Normal.Y * -127.0f), (sbyte)(v.Normal.Z * 127.0f)});

            writer.Write(0x4D434E52);
            writer.Write(145 * 3);
            writer.WriteArray(normals.ToArray());
            writer.Write(new byte[13]);
        }

        private void SaveMccv(BinaryWriter writer, int basePosition, ref Mcnk header)
        {
            if (mForceMccv == false)
            {
                header.Mccv = 0;
                header.Flags &= ~0x40u;
                return;
            }

            var colors = mShadingFloats.Select(v =>
            {
                uint b = (byte)Math.Max(Math.Min((v.Z / 2.0f) * 255.0f, 255), 0);
                uint g = (byte)Math.Max(Math.Min((v.Y / 2.0f) * 255.0f, 255), 0);
                uint r = (byte)Math.Max(Math.Min((v.X / 2.0f) * 255.0f, 255), 0);
                return 0x7F000000 | (b << 16) | (g << 8) | r;
            }).ToArray();

            header.Mccv = (int)writer.BaseStream.Position - basePosition;
            writer.Write(0x4D434356);
            writer.Write(145 * 4);
            writer.WriteArray(colors.ToArray());
        }

        private void SaveLayers(BinaryWriter writer, int basePosition, ref Mcnk header)
        {
            header.NumLayers = mLayers.Length;
            if(header.NumLayers == 0)
            {
                header.Mcly = 0;
                return;
            }

            header.Mcly = (int) writer.BaseStream.Position - basePosition;
            writer.Write(0x4D434C59);
            writer.Write(mLayers.Length * SizeCache<Mcly>.Size);
            writer.WriteArray(mLayers);
        }

        private void SaveAlpha(BinaryWriter writer, int basePosition, ref Mcnk header)
        {
            header.Mcal = (int) writer.BaseStream.Position - basePosition;
            writer.Write(0x4D43414C);
            var sizePos = writer.BaseStream.Position;
            writer.Write(0);
            var curPos = 0;
            for(var i = 1; i < mLayers.Length; ++i)
            {
                bool compressed;
                var data = GetSavedAlphaForLayer(i, out compressed);
                mLayers[i].OfsMcal = curPos;
                if (compressed)
                    mLayers[i].Flags |= 0x300;
                else
                {
                    mLayers[i].Flags |= 0x100;
                    mLayers[i].Flags &= ~0x200u;
                }
                writer.Write(data);
                curPos += data.Length;
            }

            var endPos = writer.BaseStream.Position;
            writer.BaseStream.Position = sizePos;
            writer.Write((int) (endPos - sizePos - 4));
            writer.BaseStream.Position = endPos;

            header.SizeAlpha = curPos + 8;
        }

        private void SaveUnusedChunks(BinaryWriter writer, int basePosition, ref Mcnk header)
        {
            var unusedSize = 0;
            SaveUnusedChunk(writer, 0x4D435246, basePosition, ref header.Mcrf, ref unusedSize);
            SaveUnusedChunk(writer, 0x4D435348, basePosition, ref header.Mcsh, ref mHeader.SizeShadow);
            SaveUnusedChunk(writer, 0x4D435345, basePosition, ref header.Mcse, ref mHeader.NumSoundEmitters, false);
            SaveUnusedChunk(writer, 0x4D435C51, basePosition, ref header.Mclq, ref mHeader.SizeLiquid);
            SaveUnusedChunk(writer, 0x4D434C56, basePosition, ref header.Mclv, ref unusedSize);

            header.NumSoundEmitters /= 0x1C;
        }

        private void SaveUnusedChunk(BinaryWriter writer, uint signature, int basePosition, ref int offset, ref int size, bool sizeWithHeader = true)
        {
            if (mSaveChunks.ContainsKey(signature) == false)
                return;

            var cnk = mSaveChunks[signature];
            size = cnk.Size + (sizeWithHeader ? 8 : 0);
            offset = (int) writer.BaseStream.Position - basePosition;
            writer.Write(signature);
            writer.Write(cnk.Size);
            writer.Write(cnk.Data);
        }

        private float CalculateAlphaHomogenity(int layer)
        {
            var numCompressable = 1;
            var lastAlpha = (AlphaValues[0] >> (layer * 8)) & 0xFF;
            for (var i = 1; i < 4096; ++i)
            {
                var value = (AlphaValues[i] >> (layer * 8)) & 0xFF;
                if (value == lastAlpha)
                    ++numCompressable;

                lastAlpha = value;
            }

            return numCompressable / 4096.0f;
        }

        private byte[] GetSavedAlphaForLayer(int layer, out bool compressed)
        {
            compressed = false;
            var homogenity = CalculateAlphaHomogenity(layer);
            if (homogenity > 0.3f)
            {
                compressed = true;
                return GetAlphaCompressed(layer);
            }

            return GetAlphaUncompressed(layer);
        }

        private byte[] GetAlphaCompressed(int layer)
        {
            var strm = new MemoryStream();

            // step 1: find ranges of identical values
            var ranges = new List<Tuple<int, int>>();
            var lastValue = (byte) ((AlphaValues[0] >> (layer * 8)) & 0xFF);
            var curRangeStart = 0;
            for(var i = 1; i < 4096; ++i)
            {
                var cur = (byte) ((AlphaValues[i] >> (layer * 8)) & 0xFF);
                if (cur == lastValue)
                    continue;
                
                if(i - curRangeStart > 1)
                    ranges.Add(new Tuple<int, int>(curRangeStart, i));

                curRangeStart = i;
                lastValue = cur;
            }

            // step 2: Write the ranges appropriately
            var read = 0;
            while(read < 4096)
            {
                var range = ranges.Count > 0 ? ranges[0] : null;
                if(range != null && range.Item1 == read)
                {
                    var value = (byte) ((AlphaValues[read] >> (layer * 8)) & 0xFF);
                    var repeatCount = range.Item2 - range.Item1;
                    while(repeatCount >= 0x7F)
                    {
                        strm.WriteByte(0xFF);
                        strm.WriteByte(value);
                        repeatCount -= 0x7F;
                    }

                    if(repeatCount > 0)
                    {
                        strm.WriteByte((byte)(0x80 | repeatCount));
                        strm.WriteByte(value);
                    }

                    ranges.RemoveAt(0);

                    read = range.Item2;
                }
                else
                {
                    var nextRange = ranges.Count > 0 ? ranges[0] : null;
                    int repeatCount;
                    if (nextRange == null)
                        repeatCount = 4096 - read;
                    else
                        repeatCount = nextRange.Item1 - read;

                    while(repeatCount >= 0x7F)
                    {
                        strm.WriteByte(0x7F);
                        for (var i = 0; i < 0x7F; ++i)
                            strm.WriteByte((byte) ((AlphaValues[read++] >> (layer * 8)) & 0xFF));

                        repeatCount -= 0x7F;
                    }

                    if(repeatCount > 0)
                    {
                        strm.WriteByte((byte) repeatCount);
                        for (var i = 0; i < repeatCount; ++i)
                            strm.WriteByte((byte) ((AlphaValues[read++] >> (layer * 8)) & 0xFF));
                    }
                }
            }

            return strm.ToArray();
        }

        private byte[] GetAlphaUncompressed(int layer)
        {
            if(WorldFrame.Instance.MapManager.HasNewBlend)
            {
                var ret = new byte[4096];
                for (var i = 0; i < 4096; ++i)
                    ret[i] = (byte)((AlphaValues[i] >> (layer * 8)) & 0xFF);
                return ret;
            }
            else
            {
                var ret = new byte[2048];
                for(var i = 0; i < 2048; ++i)
                {
                    var a1 = (byte) ((AlphaValues[i * 2] >> (layer * 8)) & 0xFF);
                    var a2 = (byte) ((AlphaValues[i * 2 + 1] >> (layer * 8)) & 0xFF);

                    var v1 = (uint) ((a1 / 255.0f) * 15.0f);
                    var v2 = (uint) ((a2 / 255.0f) * 15.0f);
                    ret[i] = (byte) ((v2 << 4) | v1);
                }

                return ret;
            }
        }

        static MapChunk()
        {
            var indices = Indices;
            for (uint y = 0; y < 8; ++y)
            {
                for (uint x = 0; x < 8; ++x)
                {
                    var i = y * 8 * 12 + x * 12;
                    indices[i + 0] = y * 17 + x;
                    indices[i + 2] = y * 17 + x + 1;
                    indices[i + 1] = y * 17 + x + 9;

                    indices[i + 3] = y * 17 + x + 1;
                    indices[i + 5] = y * 17 + x + 18;
                    indices[i + 4] = y * 17 + x + 9;

                    indices[i + 6] = y * 17 + x + 18;
                    indices[i + 8] = y * 17 + x + 17;
                    indices[i + 7] = y * 17 + x + 9;

                    indices[i + 9] = y * 17 + x + 17;
                    indices[i + 11] = y * 17 + x;
                    indices[i + 10] = y * 17 + x + 9;
                }
            }
        }
    }
}