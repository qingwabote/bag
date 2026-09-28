using System;
using Bastard;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace Bag
{
    public struct BatchContext
    {
        internal ComponentTypeHandle<LocalToWorld> LocalToWorld;

        public MaterialProperty.Cache MaterialPropertyCache;

        public BatchContext(EntityManager entityManager)
        {
            MaterialPropertyCache = new(entityManager);
            LocalToWorld = entityManager.GetComponentTypeHandle<LocalToWorld>(true);
        }

        public void Dispose()
        {
            MaterialPropertyCache.Dispose();
        }
    }

    public struct BatchQueue
    {
        internal readonly struct BatchKey : IEquatable<BatchKey>
        {
            internal readonly UnityObjectRef<Material> Material;

            internal readonly UnityObjectRef<Mesh> Mesh;

            internal readonly int Key;

            internal BatchKey(UnityObjectRef<Material> material, UnityObjectRef<Mesh> mesh, int key)
            {
                Material = material;
                Mesh = mesh;
                Key = key;
            }

            public override int GetHashCode()
            {
                int hash = 17;
                hash = hash * 31 + Material.GetHashCode();
                hash = hash * 31 + Mesh.GetHashCode();
                hash = hash * 31 + Key;
                return hash;
            }

            public bool Equals(BatchKey other)
            {
                return Material == other.Material && Mesh == other.Mesh && Key == other.Key;
            }
        }

        public ref struct Batcher
        {
            private readonly BatchQueue m_Queue;

            private ArchetypeChunk m_Chunk;

            private const int ChunkBatchCapacity = 32;
            private const int ChunkElementCapacity = 128;

            private unsafe fixed int m_BatchSet[ChunkBatchCapacity];
            private unsafe fixed int m_BatchToProperty[ChunkBatchCapacity];
            private int m_BatchCount;

            private unsafe fixed int m_ElementToBatch[ChunkElementCapacity];
            private unsafe fixed int m_ElementToEntity[ChunkElementCapacity];
            private int m_ElementCount;

            internal Batcher(in BatchQueue queue, in ArchetypeChunk chunk)
            {
                m_Queue = queue;
                m_Chunk = chunk;
                m_ElementCount = 0;
                m_BatchCount = 0;
            }

            public unsafe ref Batch Add(UnityObjectRef<Material> material, UnityObjectRef<Mesh> mesh, int entity, int element = 0, int hashCode = 0)
            {
                var key = new BatchKey(material, mesh, hashCode);
                ref var state = ref m_Queue.m_States->EnsureValueRef(key, out var uninitialized);
                if (uninitialized)
                {
                    state.Index = -1;
                    state.Capacity = 1;
                }

                if (state.Index == -1)
                {
                    state.Index = m_Queue.m_Queue->Length;
                    m_Queue.m_Queue->Add(new Batch(state.Capacity, material, mesh, Allocator.Temp));
                }

                var batchIndex = state.Index;

                var batchSlot = -1;
                for (int i = 0; i < m_BatchCount; i++)
                {
                    if (m_BatchSet[i] == batchIndex)
                    {
                        batchSlot = i;
                        break;
                    }
                }

                if (batchSlot == -1)
                {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    if (m_BatchCount >= ChunkBatchCapacity)
                    {
                        throw new InvalidOperationException($"ChunkScope batch capacity exceeded: {ChunkBatchCapacity}.");
                    }
#endif
                    batchSlot = m_BatchCount;
                    m_BatchSet[m_BatchCount++] = batchIndex;
                }

#if ENABLE_UNITY_COLLECTIONS_CHECKS
                if (m_ElementCount >= ChunkElementCapacity)
                {
                    throw new InvalidOperationException($"ChunkScope element capacity exceeded: {ChunkElementCapacity}.");
                }
#endif
                m_ElementToBatch[m_ElementCount] = batchSlot;
                m_ElementToEntity[m_ElementCount] = entity;
                m_ElementCount++;

                return ref m_Queue.m_Queue->ElementAt(batchIndex);
            }

            public unsafe void Dispose()
            {
                var properties = m_Queue.m_Context->MaterialPropertyCache.GetProperty(m_Chunk.Archetype);
                for (int i = 0; i < properties.Length; i++)
                {
                    var property = properties.Ptr[i];
                    for (int j = 0; j < m_BatchCount; j++)
                    {
                        var batchIndex = m_BatchSet[j];
                        ref var batch = ref m_Queue.m_Queue->ElementAt(batchIndex);
                        m_BatchToProperty[j] = batch.PropertyDataEnsure(property.Name, property.TypeSize, batch.LocalToWorlds.Capacity);
                    }

                    ref var handle = ref m_Queue.m_Context->MaterialPropertyCache.Handles.ElementAt(property.TypeIndex);

                    if (property.TypeIsBuffer)
                    {
                        var bufferAccessor = m_Chunk.GetUntypedBufferAccessor(ref handle);
                        var elementIndex = 0;
                        while (elementIndex < m_ElementCount)
                        {
                            var entity = m_ElementToEntity[elementIndex];
                            var ptr = (byte*)bufferAccessor.GetUnsafeReadOnlyPtr(entity);
                            var element = 0;
                            do
                            {
                                var batchSlot = m_ElementToBatch[elementIndex];
                                ref var batch = ref m_Queue.m_Queue->ElementAt(m_BatchSet[batchSlot]);
                                batch.PropertyDataAdd(m_BatchToProperty[batchSlot], ptr + element * property.TypeSize, property.TypeSize);
                                elementIndex++;
                                element++;
                            }
                            while (elementIndex < m_ElementCount && m_ElementToEntity[elementIndex] == entity);
                        }
                    }
                    else
                    {
                        var ptr = (byte*)m_Chunk.GetDynamicComponentDataArrayReinterpret<byte>(ref handle, property.TypeSize).GetUnsafeReadOnlyPtr();
                        for (int elementIndex = 0; elementIndex < m_ElementCount; elementIndex++)
                        {
                            var batchSlot = m_ElementToBatch[elementIndex];
                            ref var batch = ref m_Queue.m_Queue->ElementAt(m_BatchSet[batchSlot]);
                            batch.PropertyDataAdd(m_BatchToProperty[batchSlot], ptr + m_ElementToEntity[elementIndex] * property.TypeSize, property.TypeSize);
                        }
                    }
                }

                var localToWorlds = (LocalToWorld*)m_Chunk.GetNativeArray(ref m_Queue.m_Context->LocalToWorld).GetUnsafeReadOnlyPtr();
                for (int i = 0; i < m_ElementCount; i++)
                {
                    ref var batch = ref m_Queue.m_Queue->ElementAt(m_BatchSet[m_ElementToBatch[i]]);
                    batch.LocalToWorlds.Add(localToWorlds[m_ElementToEntity[i]].Value);
                }
            }

        }

        internal struct BatchState
        {
            internal int Index;
            internal int Capacity;
        }

        private readonly unsafe BatchContext* m_Context;

        private readonly unsafe UnsafeList<Batch>* m_Queue;
        public readonly unsafe int Length => m_Queue->Length;

        private readonly unsafe Bastard.UnsafeHashMap<BatchKey, BatchState>* m_States;

        unsafe internal BatchQueue(BatchContext* context)
        {
            m_Context = context;
            m_Queue = (UnsafeList<Batch>*)UnsafeUtility.Malloc(UnsafeUtility.SizeOf<UnsafeList<Batch>>(), UnsafeUtility.AlignOf<UnsafeList<Batch>>(), Allocator.Persistent);
            *m_Queue = new(32, Allocator.Persistent);
            m_States = (Bastard.UnsafeHashMap<BatchKey, BatchState>*)UnsafeUtility.Malloc(UnsafeUtility.SizeOf<Bastard.UnsafeHashMap<BatchKey, BatchState>>(), UnsafeUtility.AlignOf<Bastard.UnsafeHashMap<BatchKey, BatchState>>(), Allocator.Persistent);
            *m_States = new(128, Allocator.Persistent);
        }

        public readonly Batcher Auto(in ArchetypeChunk chunk)
        {
            return new Batcher(this, chunk);
        }

        public unsafe UnsafeList<Batch> Dump()
        {
            foreach (var kv in *m_States)
            {
                ref var state = ref kv.Value;
                if (state.Index == -1)
                {
                    continue;
                }

                ref var batch = ref m_Queue->ElementAt(state.Index);
                state.Capacity = math.max(batch.Count, state.Capacity);
                state.Index = -1;
            }

            var result = *m_Queue;
            *m_Queue = new(result.Length, Allocator.Persistent);
            return result;
        }

        public unsafe void Dispose()
        {
            m_Queue->Dispose();
            UnsafeUtility.Free(m_Queue, Allocator.Persistent);
            m_States->Dispose();
            UnsafeUtility.Free(m_States, Allocator.Persistent);
        }
    }
}
