// Resident scene description (spec §7.2): .usst header + NodeTable + chunk
// index, plus path resolution for the .usc chunk files.

using System;
using System.IO;
using Unity.Collections;

namespace GsplatLod
{
    public sealed class SplatSceneAsset : IDisposable
    {
        UsstData m_data;
        string m_directory;
        string m_baseName;

        public UsstHeader Header => m_data.Header;
        public NativeArray<NodeRecord>.ReadOnly Nodes => m_data.Nodes.AsReadOnly();
        public NativeArray<ChunkIndexEntry>.ReadOnly Chunks => m_data.Chunks.AsReadOnly();

        /// <summary>Raw NodeTable for use as a [ReadOnly] job field.</summary>
        public NativeArray<NodeRecord> NodesArray => m_data.Nodes;

        public string ChunkPath(uint chunkId) =>
            Path.Combine(m_directory, SplatFormat.ChunkFileName(m_baseName, chunkId));

        public static SplatSceneAsset Load(string usstPath, Allocator allocator)
        {
            var asset = new SplatSceneAsset
            {
                m_directory = Path.GetDirectoryName(usstPath),
                m_baseName = Path.GetFileNameWithoutExtension(usstPath),
                m_data = UsstReader.Read(usstPath, allocator),
            };
            // chunk 0 must exist (it holds the tree root and is pinned at runtime).
            string chunk0 = asset.ChunkPath(0);
            if (!File.Exists(chunk0))
            {
                asset.Dispose();
                throw new FileNotFoundException($"chunk 0 missing: {chunk0}");
            }
            return asset;
        }

        public void Dispose()
        {
            m_data?.Dispose();
            m_data = null;
        }
    }
}
