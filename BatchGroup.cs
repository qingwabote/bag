using Bastard;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;

namespace Bag
{
    [WorldSystemFilter(WorldSystemFilterFlags.Default | WorldSystemFilterFlags.Editor)]
    [UpdateInGroup(typeof(PresentationSystemGroup))]
    public unsafe partial class BatchGroup : ComponentSystemGroup
    {
        private BatchContext* m_Context;
        public BatchContext* Context => m_Context;

        public BatchQueue CreateQueue()
        {
            return new BatchQueue(m_Context);
        }

        protected override void OnCreate()
        {
            m_Context = (BatchContext*)UnsafeUtility.Malloc(UnsafeUtility.SizeOf<BatchContext>(), UnsafeUtility.AlignOf<BatchContext>(), Allocator.Persistent);
            *m_Context = new BatchContext(EntityManager);

            base.OnCreate();
        }

        protected override void OnDestroy()
        {
            m_Context->Dispose();
            UnsafeUtility.Free(m_Context, Allocator.Persistent);
            m_Context = null;

            base.OnDestroy();
        }

        protected override void OnUpdate()
        {
            for (int i = 0; i < m_Context->MaterialPropertyCache.Handles.Length; i++)
            {
                m_Context->MaterialPropertyCache.Handles.ElementAt(i).Update(this);
            }

            m_Context->LocalToWorld.Update(this);

            base.OnUpdate();
        }
    }
}
