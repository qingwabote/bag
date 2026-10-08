using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Bastard;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using static Unity.Collections.AllocatorManager;

namespace Bag
{
    public unsafe struct Batch
    {
        private struct PropertyDataStore
        {
            private const int Capacity = 7;

            private int m_Count;
            private fixed int m_Names[Capacity];
            private fixed ulong m_Ptrs[Capacity];
            private fixed short m_Locations[Capacity];
            private fixed ushort m_Capacities[Capacity];
            private fixed ushort m_Sizes[Capacity];
            private fixed byte m_ArrayTypes[Capacity];

            public readonly int Count
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => m_Count;
            }

            public int Ensure(int name, int size, int capacity)
            {
                var index = -1;
                for (int i = 0; i < m_Count; i++)
                {
                    if (m_Names[i] == name)
                    {
                        index = i;
                        break;
                    }
                }

                if (index == -1)
                {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    if (m_Count >= Capacity)
                    {
                        throw new InvalidOperationException($"Batch material property count exceeds {Capacity}.");
                    }
#endif
                    index = m_Count++;
                    m_Names[index] = name;
                    m_ArrayTypes[index] = (byte)(size == sizeof(float) ? ArrayType.Float : ArrayType.Vector);
                    m_Locations[index] = -1;
                    m_Capacities[index] = 0;
                    m_Sizes[index] = 0;
                    m_Ptrs[index] = 0;

                    Resize(index, capacity * size);
                }

                return index;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public readonly int GetName(int index)
            {
                return m_Names[index];
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public readonly ArrayType GetArrayType(int index)
            {
                return (ArrayType)m_ArrayTypes[index];
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public readonly int GetLocation(int index)
            {
                return m_Locations[index];
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public readonly int GetSize(int index)
            {
                return m_Sizes[index];
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public readonly byte* GetPtr(int index)
            {
                return (byte*)m_Ptrs[index];
            }

            public void AddBytes(int index, byte* src, int count)
            {
                var size = GetSize(index);
                if (size + count > m_Capacities[index])
                {
                    Resize(index, size + count);
                }

                UnsafeUtility.MemCpy(GetPtr(index) + size, src, count);

                m_Sizes[index] = (ushort)(size + count);
            }

            private void Resize(int index, int capacity)
            {
                var arrayType = GetArrayType(index);
                var size = arrayType == ArrayType.Float ? sizeof(float) : UnsafeUtility.SizeOf<Vector4>();
                var arrayLength = (ushort)math.ceil(capacity / (float)size);
                void*
#if UNITY_WEBGL && !UNITY_EDITOR
                ptr = ArrayAllocatorManaged.Alloc(arrayType, ref arrayLength, out short location);
#else
                ptr = ArrayAllocator.Alloc.Data.Invoke(arrayType, ref arrayLength, out short location);
#endif
                var oldSize = GetSize(index);
                if (oldSize > 0)
                {
                    UnsafeUtility.MemCpy(ptr, GetPtr(index), oldSize);
                }

                m_Locations[index] = location;
                m_Capacities[index] = (ushort)(size * arrayLength);
                m_Ptrs[index] = (ulong)ptr;
            }
        }

        public NativeList<float4x4> LocalToWorlds;

        private PropertyDataStore m_PropertyData;

        private FixedList32Bytes<int> m_TextureNames;
        private FixedList32Bytes<UnityObjectRef<Texture>> m_TextureValues;

        public readonly UnityObjectRef<Material> Material;
        public readonly UnityObjectRef<Mesh> Mesh;

        public readonly int Count => LocalToWorlds.Length;
        public readonly int Capacity => LocalToWorlds.Capacity;
        public readonly bool PropertyAcquired => m_PropertyData.Count > 0;

        public Batch(int capacity, UnityObjectRef<Material> material, UnityObjectRef<Mesh> mesh, AllocatorHandle allocator)
        {
            LocalToWorlds = new(capacity, allocator);

            m_PropertyData = new();

            m_TextureNames = new();
            m_TextureValues = new();

            Material = material;
            Mesh = mesh;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void PropertyTextureBind(int name, UnityObjectRef<Texture> texture)
        {
            m_TextureNames.Add(name);
            m_TextureValues.Add(texture);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int PropertyDataEnsure(int name, int size, int capacity)
        {
            return m_PropertyData.Ensure(name, size, capacity);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void PropertyDataAdd(int index, byte* src, int size)
        {
            m_PropertyData.AddBytes(index, src, size);
        }

        private static readonly List<float> s_FloatList = new();
        private static readonly List<Vector4> s_VectorList = new();

        public readonly void PropertyToBlock(MaterialPropertyBlock output)
        {
            for (int i = 0; i < m_TextureNames.Length; i++)
            {
                output.SetTexture(m_TextureNames[i], m_TextureValues[i]);
            }

            for (int i = 0; i < m_PropertyData.Count; i++)
            {
                if (m_PropertyData.GetArrayType(i) == ArrayType.Float)
                {
                    NoAllocHelpers.ResetListContents(s_FloatList, (float[])ArrayAllocatorManaged.Get(m_PropertyData.GetLocation(i)), m_PropertyData.GetSize(i) / sizeof(float));
                    output.SetFloatArray(m_PropertyData.GetName(i), s_FloatList);
                }
                else
                {
                    NoAllocHelpers.ResetListContents(s_VectorList, (Vector4[])ArrayAllocatorManaged.Get(m_PropertyData.GetLocation(i)), m_PropertyData.GetSize(i) / UnsafeUtility.SizeOf<Vector4>());
                    output.SetVectorArray(m_PropertyData.GetName(i), s_VectorList);
                }
            }
        }

        public readonly void PropertyToBlock(int index, MaterialPropertyBlock output)
        {
            for (int i = 0; i < m_TextureNames.Length; i++)
            {
                output.SetTexture(m_TextureNames[i], m_TextureValues[i]);
            }

            for (int i = 0; i < m_PropertyData.Count; i++)
            {
                if (m_PropertyData.GetArrayType(i) == ArrayType.Float)
                {
                    output.SetFloat(m_PropertyData.GetName(i), *(float*)(m_PropertyData.GetPtr(i) + index * sizeof(float)));
                }
                else
                {
                    output.SetVector(m_PropertyData.GetName(i), *(Vector4*)(m_PropertyData.GetPtr(i) + index * sizeof(Vector4)));
                }
            }
        }
    }
}
