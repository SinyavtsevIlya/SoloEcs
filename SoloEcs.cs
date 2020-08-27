#define SOLOECS_LAZYPOOLS
//#define SOLOECS_REACTIVE
#define SOLOECS_DI
#define SOLOECS_SINGLEWORLD

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
#if SOLOECS_DI
using System.Reflection; 
#endif

namespace SoloEcs {
#if ENABLE_IL2CPP
    using Unity.IL2CPP.CompilerServices;
#endif
    public class Systems : IUpdateSystem, IInitializeSystem, IDestroySystem {
        public ResizableArray<IUpdateSystem> UpdateSystems = new ResizableArray<IUpdateSystem>(32);
        public ResizableArray<ILateUpdateSystem> LateUpdateSystems = new ResizableArray<ILateUpdateSystem>(32);
        public ResizableArray<IInitializeSystem> InitializeSystems = new ResizableArray<IInitializeSystem>(32);
        public ResizableArray<IDestroySystem> DestroySystems = new ResizableArray<IDestroySystem>(32);
        public ResizableArray<ISystem> AllSystems = new ResizableArray<ISystem>(32);
        public ResizableArray<World> Worlds = new ResizableArray<World>(8);
        ResizableArray<DirtyPair>[] _dirtyMarkers;
        ResizableArray<DirtyPair>[] _addedMarkers;
#if SOLOECS_DI
        Dictionary<Type, object> _dependencies = new Dictionary<Type, object>(32);
        Dictionary<Type, object[]> _optionalArguments = new Dictionary<Type, object[]>(32);
#endif

        public void Initialize() {
#if SOLOECS_DI
            for (int i = 0; i < AllSystems.Count; i++) {
                var system = AllSystems.Values[i];
                var systemType = system.GetType();

                var fields = systemType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                bool hasArgs = _optionalArguments.ContainsKey(systemType);

                for (var fIdx = 0; fIdx < fields.Length; fIdx++) {
                    var field = fields[fIdx];
                    if (!field.IsInitOnly) continue;
                    var type = field.FieldType;
                    bool hasOptionalArgMatched = false;
                    if (hasArgs) {
                        var args = _optionalArguments[systemType];
                        for (int argIdx = 0; argIdx < args.Length; argIdx++) {
                            if (field.FieldType.IsAssignableFrom(args[argIdx].GetType())) {
                                field.SetValue(system, args[argIdx]);
                                hasOptionalArgMatched = true;
                            }
                        }
                    }
                    bool hasProvider = _dependencies.ContainsKey(type);
                    if (hasProvider) {
                        if (!hasOptionalArgMatched) {
                            field.SetValue(system, _dependencies[type]);
                        }
                    }

                    if (!hasProvider && !hasOptionalArgMatched) {
                        throw new Exception($"readonly field <b>{field.Name}</b> in <b>{systemType.Name}</b> can not be resolved. No matching provider or optional argument found.");
                    }
                }
            } 
#endif

            _dirtyMarkers = new ResizableArray<DirtyPair>[Worlds.Count];
            _addedMarkers = new ResizableArray<DirtyPair>[Worlds.Count];
            for (int worldIdx = 0; worldIdx < Worlds.Count; worldIdx++) {
                _dirtyMarkers[worldIdx] = new ResizableArray<DirtyPair>(32);
                _addedMarkers[worldIdx] = new ResizableArray<DirtyPair>(32);
            }

            for (int i = 0, len = InitializeSystems.Count; i < len; i++) {
                InitializeSystems.Values[i].Initialize();
            }
        }
        public void Destroy() {
            for (int i = 0, len = DestroySystems.Count; i < len; i++) {
                DestroySystems.Values[i].Destroy();
            }
        }

        public Systems Add(ISystem system) {
            if (system is IUpdateSystem uSystem) UpdateSystems.Add(uSystem);
            if (system is ILateUpdateSystem luSystem) LateUpdateSystems.Add(luSystem);
            if (system is IInitializeSystem iSystem) InitializeSystems.Add(iSystem);
            if (system is IDestroySystem dSystem) DestroySystems.Add(dSystem);
            AllSystems.Add(system);
            return this;
        }

#if SOLOECS_DI
        public Systems Add<TSystem>(params object[] args) where TSystem : ISystem {
            var system = Activator.CreateInstance<TSystem>();
            _optionalArguments[typeof(TSystem)] = args;
            Add(system);
            AllSystems.Add(system);
            return this;
        }
#endif

#if SOLOECS_DI
        public Systems Inject(object injectable) {
            if (injectable == null) throw new Exception("Unable to inject null values");

            _dependencies[injectable.GetType()] = injectable;
            return this;
        }
        public Systems Inject<TProvider>() {
            var injectable = System.Activator.CreateInstance<TProvider>();
            Inject(injectable);
            return this;
        }

        public Systems Inject<TProvider, TContract>() {
            var injectable = System.Activator.CreateInstance<TProvider>();
            if (!typeof(TContract).IsAssignableFrom(typeof(TProvider))) {
                throw new Exception($"Unable to create binding. <b>{typeof(TContract)}</b> is not assignable from <b>{typeof(TProvider)}</b>. ");
            }
            _dependencies[typeof(TContract)] = injectable;
            return this;
        }
#endif
        public Systems AddWorld(World world) {
#if SOLOECS_DI
            _dependencies[world.GetType()] = world;
#endif
            Worlds.Add(world);
            return this;
        }

        public void Update() {
            for (int systemIdx = 0, len = UpdateSystems.Count; systemIdx < len; systemIdx++) {
                UpdateSystems.Values[systemIdx].Update();
                for (int worldIdx = 0; worldIdx < Worlds.Count; worldIdx++) {
                    TryCacheMarkers(ref Worlds.Values[worldIdx].DirtyMarker, worldIdx, _dirtyMarkers);
                    TryCacheMarkers(ref Worlds.Values[worldIdx].AddedMarker, worldIdx, _addedMarkers);
                }
            }
        }

        void TryCacheMarkers(ref DirtyPair worldMarker, int worldIdx, ResizableArray<DirtyPair>[] markers) {

            if (worldMarker.IsDirty()) {
                markers[worldIdx].Add(worldMarker);
                worldMarker = DirtyPair.Empty;
            }
        }

        public void LateUpdate() {
            for (int systemIdx = 0, len = LateUpdateSystems.Count; systemIdx < len; systemIdx++) {
                LateUpdateSystems.Values[systemIdx].LateUpdate();
            }
            ClearSets(_dirtyMarkers);
            ClearSets(_addedMarkers);
        }

        void ClearSets(ResizableArray<DirtyPair>[] markers) {
            for (int worldIdx = 0; worldIdx < Worlds.Count; worldIdx++) {
                for (int markerIdx = 0; markerIdx < markers[worldIdx].Count; markerIdx++) {
                    ref var marker = ref markers[worldIdx].Values[markerIdx];
                    Worlds.Values[worldIdx].Entities.With(marker.OriginalID)
                        .ClearAll(marker.DirtyID)
                        .Dispose();
                }
                markers[worldIdx].Clear();
            }
        }


    }

    public interface ISystem { }
    public interface IUpdateSystem : ISystem { void Update(); }
    public interface ILateUpdateSystem : ISystem { void LateUpdate(); }
    public interface IInitializeSystem : ISystem { void Initialize(); }
    public interface IDestroySystem : ISystem { void Destroy(); }

    public class World {
        internal const int FiltersPoolSize = 4;

        public int Index;
        int _entitiesCount;

        internal Entity[] entities;
        internal readonly int Capacity;
        internal DirtyPair DirtyMarker;
        internal DirtyPair AddedMarker;
        internal Filter[] FiltersPool;
        internal int CurrentFilterIdx;

        int[] _reservedEntities;
        int _reservedEntitiesCount;

        public World(int capacity) {
            Index = WorldsState.Count++;
            Capacity = capacity;
            entities = new Entity[64];
            _reservedEntities = new int[64];
            
            FiltersPool = new Filter[FiltersPoolSize];
            for (int i = 0; i < FiltersPoolSize; i++) {
                FiltersPool[i] = new Filter(this);
            }
            WorldsState.WorldsPools[Index] = new ResizableArray<PoolBase>(WorldsState.MaxComponentsCount);
            DirtyMarker = DirtyPair.Empty;
            AddedMarker = DirtyPair.Empty;
        }

#if !SOLOECS_LAZYPOOLS
        public World RegisterComponent<T>(bool isReactive = false) where T : struct {
            ComponentLayer<T>.TryRegister(Index, Capacity);
            if (isReactive) ComponentLayer<Dirty<T>>.TryRegister(Index, Capacity);
#if SOLOECS_REACTIVE
            ComponentLayer<Added<T>>.TryRegister(Index, Capacity); 
#endif
            return this;
        }
#endif

        public Entity CreateEntity() {
            int id;
            if (_reservedEntitiesCount > 0) {
                _reservedEntitiesCount--;
                id = _reservedEntities[_reservedEntitiesCount];
            }
            else {
                id = _entitiesCount;
            }
            _entitiesCount++;

            var entity = new Entity() { World = this };

            if (id == entities.Length) {
                Array.Resize(ref entities, _entitiesCount << 1);
            }

            entities[id] = entity;

            entity.Id = id;
            entity.IsReserved = false;

            return entity;
        }

        public void Destroy(int id) {
            if (_entitiesCount == 0) return;
            Destroy(entities[id]);
        }

        public void Destroy(Entity entity) {
            _entitiesCount--;

            if (entity.IsReserved) throw new System.Exception("Unable to destroy reserved entity " + entity.Id);

            entity.IsReserved = true;

            _reservedEntities[_reservedEntitiesCount++] = entity.Id;

            if (_reservedEntitiesCount == _reservedEntities.Length) {
                Array.Resize(ref _reservedEntities, _reservedEntitiesCount << 1);
            }

            entity.Gen++;
        }

        public Entity GetEntity(int id) {
            if (id < 0) return Entity.Empty;
            var entity = entities[id];
            if (entity.IsReserved) return Entity.Empty;
            return entity;
        }

        public Filter Entities => FiltersPool[CurrentFilterIdx++];
        public Filter AddedEntities {
            get {
                var filter = FiltersPool[CurrentFilterIdx++];

                filter.Mode = Filter.Modes.Added;
                return filter;
            }
        }

        internal void SetDirty<TComponent>() where TComponent : struct {
            DirtyMarker = new DirtyPair() {
                DirtyID = ComponentLayer<Dirty<TComponent>>.Indecies[Index],
                OriginalID = ComponentLayer<TComponent>.Indecies[Index]
            };
        }

        internal void SetAdded<TComponent>() where TComponent : struct {
            AddedMarker = new DirtyPair() {
                DirtyID = ComponentLayer<Added<TComponent>>.Indecies[Index],
                OriginalID = ComponentLayer<TComponent>.Indecies[Index]
            };
        }
    }

    internal struct Dirty<T> where T : struct { }
    internal struct Added<T> where T : struct { }

    internal struct DirtyPair {
        internal int OriginalID;
        internal int DirtyID;
        internal static DirtyPair Empty = new DirtyPair() { DirtyID = -1, OriginalID = -1 };
        internal bool IsDirty() => OriginalID > -1;
    }

    public class Entity {
        public static Entity Empty = new Entity() { Id = -1 };

        public int Id;
        public ushort Gen;
        public bool IsReserved;
        internal World World;


        public void Toggle<TComponent>() where TComponent : struct {
            var pool = ComponentLayer<TComponent>.WorldPools[World.Index];
            pool.ToogleAtIndex(Id, this);
        }

        public ref TComponent Add<TComponent>() where TComponent : struct {
#if SOLOECS_LAZYPOOLS   
            ComponentLayer<TComponent>.TryRegister(World.Index, World.Capacity);
#if SOLOECS_REACTIVE
            ComponentLayer<Added<TComponent>>.TryRegister(World.Index, World.Capacity); 
#endif
#endif
            var pool = ComponentLayer<TComponent>.WorldPools[World.Index];
#if SOLOECS_REACTIVE
            ComponentLayer<Added<TComponent>>.WorldPools[World.Index].ActivateAtIndexNoItemsTracking(Id);
            World.SetAdded<TComponent>();
#endif

#if UNITY_EDITOR && !SOLOECS_LAZYPOOLS
            if (pool == null) {
                throw new Exception($"Component of type {typeof(TComponent)} is not registered. Use \"World.RegisterComponent()\" or enable \"AUTOPOOLS\" directive");
            }
#endif
            ref var c = ref pool.ActivateAtIndex(Id, this);
            return ref c;
        }

        public void Remove<TComponent>() where TComponent : struct {
            ComponentLayer<TComponent>.WorldPools[World.Index].DeactivateAtIndex(Id);
        }

        public ref TComponent Get<TComponent>() where TComponent : struct {
#if SOLOECS_SINGLEWORLD
            return ref ComponentLayer<TComponent>.Singleton.items[Id];
#else
            return ref ComponentLayer<TComponent>.WorldPools[World.Index].items[Id];
#endif
        }

        public ref TComponent GetDirty<TComponent>() where TComponent : struct {
            var worldId = World.Index;
            ref var c = ref ComponentLayer<TComponent>.WorldPools[worldId].GetAtIndex(Id);
#if SOLOECS_LAZYPOOLS
            ComponentLayer<Dirty<TComponent>>.TryRegister(World.Index, World.Capacity);
#endif
            ComponentLayer<Dirty<TComponent>>.WorldPools[worldId].ActivateAtIndexNoItemsTracking(Id);
            World.SetDirty<TComponent>();
            return ref c;
        }

        public bool Has<TComponent>() where TComponent : struct {
            return ComponentLayer<TComponent>.WorldPools[World.Index].HasComponentAtIndex(Id);
        }
    }

    internal static class WorldsState {
        internal static int Count;
        internal static int MaxComponentsCount = 512;
        internal static int[] TypeIndeciesCounts = new int[16]; // TODO: Replace with actual worlds count
        internal static ResizableArray<PoolBase>[] WorldsPools = new ResizableArray<PoolBase>[16];
    }

    internal class ComponentLayer<T> where T : struct {
        public static Pool<T>[] WorldPools;
#if SOLOECS_SINGLEWORLD
        public static Pool<T> Singleton; 
#endif
        public static int[] Indecies;
        static ComponentLayer() {
#if SOLOECS_SINGLEWORLD
            Singleton = new Pool<T>(1024); 
#endif
            WorldPools = new Pool<T>[WorldsState.Count];
            Indecies = new int[WorldsState.Count];
            for (int i = 0; i < WorldsState.Count; i++) {
                Indecies[i] = -1;
            }
        }

        internal static void TryRegister(int worldIndex, int capacity) {
            if (Indecies[worldIndex] == -1) {
                var pool = new Pool<T>(capacity);
                WorldPools[worldIndex] = pool;
#if SOLOECS_SINGLEWORLD
                Singleton = pool;
#endif
                Indecies[worldIndex] = WorldsState.TypeIndeciesCounts[worldIndex]++;
                WorldsState.WorldsPools[worldIndex].Add(pool);
            }
        }
    }

#if ENABLE_IL2CPP
    [Il2CppSetOption(Option.NullChecks, false)]
    [Il2CppSetOption(Option.ArrayBoundsChecks, false)]
#endif
    public sealed class Filter : IDisposable {
        internal enum Modes {
            Default,
            Added,
            Removed
        }
        internal Modes Mode;
        internal readonly bool[][] WithChecks;
        internal readonly bool[][] WithoutChecks;
        readonly World _world;
        int _withCount;
        EntitySparseSet _baseSet;
        int _withoutCount;
        int _currentIndex;
        int _withIndexToRemove;
        bool _isAdded;

        public Filter(World world) {
            _world = world;
            WithChecks = new bool[16][];
            WithoutChecks = new bool[16][];
        }

#if ENABLE_IL2CPP
        [Il2CppSetOption(Option.NullChecks, false)]
        [Il2CppSetOption(Option.ArrayBoundsChecks, false)]
#endif
        public Entity Current {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get {
                return _baseSet.Entities[_currentIndex];
            }
        }

#if ENABLE_IL2CPP
        [Il2CppSetOption(Option.NullChecks, false)]
        [Il2CppSetOption(Option.ArrayBoundsChecks, false)]
#endif
        public Filter GetEnumerator() {
            _currentIndex = _baseSet.Count;
            WithChecks[_withIndexToRemove] = WithChecks[--_withCount];
            return this;
        }

#if ENABLE_IL2CPP
        [Il2CppSetOption(Option.NullChecks, false)]
        [Il2CppSetOption(Option.ArrayBoundsChecks, false)]
#endif
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Dispose() {
            Mode = Modes.Default;
            _withCount = 0;
            _withoutCount = 0;
            _baseSet = null;
            _world.CurrentFilterIdx--;
        }

#if ENABLE_IL2CPP
        [Il2CppSetOption(Option.NullChecks, false)]
        [Il2CppSetOption(Option.ArrayBoundsChecks, false)]
#endif
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNext() {

Repeat:
            if (--_currentIndex < 0) return false;
            var sparseIndex = _baseSet.Dense[_currentIndex];

            for (int i = 0; i < _withCount; i++) {
                if (!WithChecks[i][sparseIndex]) {
                    goto Repeat;
                }
            }

            for (int i = 0; i < _withoutCount; i++) {
                if (WithoutChecks[i][sparseIndex]) {
                    goto Repeat;
                }
            }

            return _currentIndex >= 0;
        }

        public void Reset() {
        }

        public Entity[] WithOnly<T>() where T : struct {
#if SOLOECS_LAZYPOOLS
            ComponentLayer<T>.TryRegister(_world.Index, _world.Capacity);
#endif
            this.Dispose();
            return ComponentLayer<T>.WorldPools[_world.Index].ActiveItems.Entities;
        }

        public Filter OnChange<T>() where T : struct {
#if SOLOECS_LAZYPOOLS
            ComponentLayer<Dirty<T>>.TryRegister(_world.Index, _world.Capacity);
#endif
            var pool =
#if SOLOECS_SINGLEWORLD
                ComponentLayer<Dirty<T>>.Singleton;
#else
                ComponentLayer<Dirty<T>>.WorldPools[_world.Index];
#endif
            WithChecks[_withCount++] = pool.HasItem;
            return this;
        }

        public Filter With<T>() where T : struct {
#if SOLOECS_LAZYPOOLS
            ComponentLayer<T>.TryRegister(_world.Index, _world.Capacity);
            ComponentLayer<Added<T>>.TryRegister(_world.Index, _world.Capacity);
#endif
            switch (Mode) {
                case Modes.Default:
                    HandleWith(ComponentLayer<T>.WorldPools[_world.Index]);
                    break;
                case Modes.Added:
                    var originalPool = ComponentLayer<T>.WorldPools[_world.Index];
                    HandleWith(originalPool);
                    var dirtyPool = ComponentLayer<Added<T>>.WorldPools[_world.Index];
                    WithChecks[_withCount++] = dirtyPool.HasItem;
                    break;
                case Modes.Removed:
                    break;
                default:
                    break;
            }
            
            return this;
        }

        public Filter With(int componentIndex) {
            var pool = WorldsState.WorldsPools[_world.Index].Values[componentIndex];
            HandleWith(pool);
            return this;
        }

        public Filter Without<T>() where T : struct {
#if SOLOECS_LAZYPOOLS
            ComponentLayer<T>.TryRegister(_world.Index, _world.Capacity);
            ComponentLayer<Added<T>>.TryRegister(_world.Index, _world.Capacity);
#endif
            WithoutChecks[_withoutCount++] = ComponentLayer<T>.WorldPools[_world.Index].HasItem;
            return this;
        }

        public Filter Without(int componentIndex) {
            var pool = WorldsState.WorldsPools[_world.Index].Values[componentIndex];
            WithoutChecks[_withoutCount++] = pool.HasItem;
            return this;
        }

        internal Filter ClearAll(int componentIndex) {
            _currentIndex = _baseSet.Count;
            WithChecks[_withIndexToRemove] = WithChecks[--_withCount];
            var pool = WorldsState.WorldsPools[_world.Index].Values[componentIndex];
Repeat:
            if (--_currentIndex < 0) return this;
            var sparseIndex = _baseSet.Dense[_currentIndex];

            pool.HasItem[sparseIndex] = false;
            goto Repeat;
        }

        void HandleWith(PoolBase pool) {
            bool maySelectBaseSet = false;
            var currentSet = pool.ActiveItems;
            if (_baseSet == null) {
                maySelectBaseSet = true;
            }
            else {
                if (currentSet.Count < _baseSet.Count) {
                    maySelectBaseSet = true;
                }
            }
            if (maySelectBaseSet) {
                _baseSet = currentSet;
                _withIndexToRemove = _withCount;
            }

            WithChecks[_withCount++] = pool.HasItem;
        }
    }

    public class EntitySparseSet {
        public int[] Sparse;
        public int[] Dense;
        public Entity[] Entities;

        public EntitySparseSet(int capacity) {
            Sparse = new int[capacity];
            Dense = new int[capacity];
            Entities = new Entity[capacity];
        }

        public int Count;

        public bool Has(int index) => Sparse[index] < Count && Dense[Sparse[index]] == index;

        public void Add(int index, Entity e) {
            if (index >= Sparse.Length || Count >= Dense.Length) {
                Array.Resize(ref Sparse, Sparse.Length << 1);
                Array.Resize(ref Dense, Dense.Length << 1);
                Array.Resize(ref Entities, Entities.Length << 1);
            }
            Dense[Count] = index;
            Entities[Count] = e;
            Sparse[index] = Count++;
        }

        public void Remove(int index) {
            var lastID = --Count;
            var last = Dense[lastID];
            Dense[Sparse[index]] = last;
            Entities[Sparse[index]] = Entities[lastID];
            Sparse[last] = Sparse[index];
        }

        public void Clear() {
            Count = 0;
        }
    }

    public class SparseSet {
        public int[] Sparse;
        public int[] Dense;

        public SparseSet(int capacity) {
            Sparse = new int[capacity];
            Dense = new int[capacity];
        }

        public int Count;

        public bool Has(int index) => Sparse[index] < Count && Dense[Sparse[index]] == index;

        public void Add(int index, Entity e) {
            if (index >= Sparse.Length) {
                Array.Resize(ref Sparse, Sparse.Length << 1);
                Array.Resize(ref Dense, Dense.Length << 1);
            }
            Dense[Count] = index;
            Sparse[index] = Count++;
        }

        public void Remove(int index) {
            var lastID = --Count;
            var last = Dense[lastID];
            Dense[Sparse[index]] = last;
            Sparse[last] = Sparse[index];
        }

        public void Clear() {
            Count = 0;
        }
    }

    internal class PoolBase {
        internal bool[] HasItem;
        internal EntitySparseSet ActiveItems;

        public PoolBase(int capacity) {
            HasItem = new bool[capacity];
            ActiveItems = new EntitySparseSet(capacity);
        }
    }

    internal class Pool<T> : PoolBase where T : struct {
        public T[] items;

        internal Pool(int capacity) : base(capacity) {
            items = new T[capacity];
        }

        internal ref T GetAtIndex(int id) {
            return ref items[id];
        }

        internal ref T ActivateAtIndex(int id, Entity entity) {
            CheckID(id);
            if (!ActiveItems.Has(id)) {
                ActiveItems.Add(id, entity);
            }
            HasItem[id] = true;
            return ref items[id];
        }
        internal void ActivateAtIndexNoItemsTracking(int id) {
            CheckID(id);
            HasItem[id] = true;
        }

        internal void DeactivateAtIndex(int id) {
            CheckID(id);
            if (HasItem[id]) {
                if (ActiveItems.Has(id)) ActiveItems.Remove(id);
                HasItem[id] = false;
            }
        }

        internal void ToogleAtIndex(int id, Entity entity) {
            CheckID(id);
            var has = HasItem[id];
            if (has) {
                ActiveItems.Remove(id);
            }
            else {
                ActiveItems.Add(id, entity);
            }
            HasItem[id] = !has;
        }

        internal bool HasComponentAtIndex(int id) {
#if UNITY_EDITOR
            if (id >= HasItem.Length) {
                throw new Exception($"Unable to check if entity has a component {typeof(T)}. Entity with id {id} doesn't exist");
            }
#endif
            return HasItem[id];
        }

        void CheckID(int id) {
            if (id >= items.Length) {
                Array.Resize(ref items, items.Length << 1);
            }
        }
    }


#if ENABLE_IL2CPP
    namespace Unity.IL2CPP.CompilerServices {
        using System;

        public enum Option {
            NullChecks = 1,
            ArrayBoundsChecks = 2,
            DivideByZeroChecks = 3
        }

        [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Struct | AttributeTargets.Method | AttributeTargets.Property, AllowMultiple = true)]
        public class Il2CppSetOptionAttribute : Attribute {
            public Option Option { get; }
            public object Value { get; }

            public Il2CppSetOptionAttribute(Option option, object value) {
                this.Option = option;
                this.Value = value;
            }
        }
    }
#endif

    public class ResizableArray<T> {
        public T[] Values;
        public int Count;

        public ResizableArray(int capacity) {
            Values = new T[capacity];
            Count = 0;
        }

        public void Add(T item) {
            if (Values.Length == Count) {
                Array.Resize(ref Values, Values.Length << 1);
            }
            Values[Count++] = item;
        }

        public void Clear() {
            Count = 0;
        }
    }

    
}



