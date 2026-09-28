using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Rendering;
using UnityEngine;

namespace Bag
{
    public readonly struct MaterialProperty
    {
        public struct Cache
        {
            public NativeArray<DynamicComponentTypeHandle> Handles;
            private Bastard.UnsafeHashMap<EntityArchetype, UnsafeList<MaterialProperty>> m_Properties;

            public Cache(EntityManager entityManager)
            {
                Handles = new NativeArray<DynamicComponentTypeHandle>(s_PropertyTypes.Count, Allocator.Persistent);
                for (int i = 0; i < s_PropertyTypes.Count; i++)
                {
                    Handles[i] = entityManager.GetDynamicComponentTypeHandle(ComponentType.ReadOnly(s_PropertyTypes[i]));
                }

                m_Properties = new(8, Allocator.Persistent);
            }

            public void Dispose()
            {
                if (Handles.IsCreated)
                {
                    Handles.Dispose();
                }

                if (m_Properties.IsCreated)
                {
                    foreach (var kv in m_Properties)
                    {
                        kv.Value.Dispose();
                    }

                    m_Properties.Dispose();
                }
            }

            public UnsafeList<MaterialProperty>.ReadOnly GetProperty(EntityArchetype archetype)
            {
                if (m_Properties.TryGetValue(archetype, out var list))
                {
                    return list.AsReadOnly();
                }

                var types = archetype.GetComponentTypes(Allocator.Temp);

                int count = 0;
                foreach (var type in types)
                {
                    if (s_TypeToProperty.Data.ContainsKey(type.TypeIndex))
                        count++;
                }
                Debug.Assert(count <= Capacity);

                UnsafeList<MaterialProperty> properties = new(count, Allocator.Persistent);
                foreach (var type in types)
                {
                    if (s_TypeToProperty.Data.TryGetValue(type.TypeIndex, out MaterialProperty property))
                        properties.Add(property);
                }
                NativeSortExtension.Sort(properties, new PropertyComparer());

                m_Properties.Add(archetype, properties);

                return properties.AsReadOnly();
            }
        }

        struct PropertyComparer : IComparer<MaterialProperty>
        {
            public int Compare(MaterialProperty x, MaterialProperty y)
            {
                return x.Name.CompareTo(y.Name);
            }
        }

        public const int Capacity = 7;

        private struct TypeToPropertyTag { }
        // use TypeIndex of ComponentType as key, ignore AccessModeType
        static private readonly SharedStatic<Bastard.UnsafeHashMap<int, MaterialProperty>> s_TypeToProperty = SharedStatic<Bastard.UnsafeHashMap<int, MaterialProperty>>.GetOrCreate<TypeToPropertyTag>();

        static private List<TypeIndex> s_PropertyTypes = new List<TypeIndex>(8);

        static MaterialProperty()
        {
            s_TypeToProperty.Data = new(8, Allocator.Persistent);
            foreach (var typeInfo in TypeManager.AllTypes)
            {
                var type = typeInfo.Type;

                if (typeof(IComponentData).IsAssignableFrom(type) || typeof(IBufferElementData).IsAssignableFrom(type))
                {
                    var attributes = type.GetCustomAttributes(typeof(MaterialPropertyAttribute), false);
                    if (attributes.Length > 0)
                    {
                        var attribute = (MaterialPropertyAttribute)attributes[0];
                        // MaterialProperty.TypeIndex packs the slot index into the per-world handle array, so the slot and s_PropertyTypes order must stay in lockstep
                        var property = new MaterialProperty(Shader.PropertyToID(attribute.Name), s_PropertyTypes.Count, UnsafeUtility.SizeOf(type), typeInfo.TypeIndex.IsBuffer);
                        s_PropertyTypes.Add(typeInfo.TypeIndex);
                        s_TypeToProperty.Data.Add(typeInfo.TypeIndex, property);
                    }
                }
            }
        }


        public readonly int Name;

        private readonly int m_Type;
        public int TypeIndex => m_Type >> 9;
        public int TypeSize => m_Type >> 1 & 0xFF;
        public bool TypeIsBuffer => (m_Type & 0x1) != 0;

        public MaterialProperty(int name, int typeIndex, int typeSize, bool typeIsBuffer)
        {
            Name = name;
            m_Type = (typeIndex << 9) | (typeSize << 1) | (typeIsBuffer ? 1 : 0);
        }
    }
}
