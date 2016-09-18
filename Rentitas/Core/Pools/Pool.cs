﻿using System;
using System.Collections.Generic;
using System.Linq;
using Rentitas.Caching;

namespace Rentitas
{
    public class PoolMeta
    {
        public Dictionary<Type, string> ComponentNames { get; private set; }
        public Type[] ComponentTypes { get; private set; }
        public int TotalComponents { get; private set; }

        public PoolMeta(int totalComponents, Type[] types, Dictionary<Type, string> names)
        {
            TotalComponents = totalComponents;
            ComponentTypes = types;
            ComponentNames = names;
        }
    }

    public partial class Pool<T> : IPool where T : class, IComponent
    {
        public string PoolName { get; private set; }
        /// <summary>
        /// Generic type of interface extended IComponent
        /// </summary>
        public Type PoolType => typeof (T);

        /// Returns the number of entities in the pool.
        public int Count => _entities.Count;

        /// The total amount of components an entity can possibly have.
        /// This value is generated by the code generator, e.g ComponentIds.TotalComponents.
        public int TotalComponents => _totalComponents;

        /// Returns all componentPools. componentPools is used to reuse removed components.
        /// Removed components will be pushed to the componentPool.
        /// Use entity.CreateComponent(index, type) to get a new or reusable component from the componentPool.
        public Dictionary<Type, Stack<T>> ComponentPools => _componentPools;
        public PoolMeta Meta => _metaData;

        public Pool(params T[] components) : this(string.Empty, 0, components) { }

        public Pool(string name, params T[] components) : this(name, 0, components) { }
        public Pool(int creationIndex, params T[] components) : this(string.Empty, creationIndex, components) { }

        public Pool(string name, int creationIndex, params T[] components) : this(components.Length, creationIndex)
        {
            PoolName = name;
            var types = RentitasCache.GetTypeHashSet();
            for (int i = 0; i < components.Length; i++)
            {
                var component = components[i];
                var type = component.GetType();
                if(!types.Add(type))
                    throw new PoolMetaDataException<T>(this, null);

                var stack = new Stack<T>();
                stack.Push(component);
                _componentPools.Add(type, stack);
                _groupsForTypes.Add(type, new List<Group<T>>());


                var isSingleton = component is ISingleton;
                if (isSingleton)
                {
                    // TODO: Create auto single group
                    var group = GetGroup(Matcher.AllOf(type));
                    _singletons.Add(type, group);
                }
            }

            _metaData = new PoolMeta(
                types.Count, types.ToArray(),
                components.ToDictionary(
                    c => c.GetType(),
                    c => c.ToString().Split('.').Last()));

            RentitasCache.PushTypeHashSet(types);
        }

        Pool(int totalComponents) : this(totalComponents, 0) { }

        Pool(int totalComponents, int creationIndex)
        {
            _totalComponents = totalComponents;
            _creationIndex = creationIndex;
            
            _groupsForTypes = new Dictionary<Type, List<Group<T>>>(totalComponents);
            _componentPools = new Dictionary<Type, Stack<T>>(totalComponents);

            // Cache delegates to avoid gc allocations
            _cachedUpdateGroupsComponentAddedOrRemoved  = UpdateGroupsComponentAddedOrRemoved;
            _cachedUpdateGroupsComponentReplaced        = UpdateGroupsComponentReplaced;
            _cachedOnEntityReleased                     = OnEntityReleased;

        }
//        public T2 Get<T2>() where T2 : T, new()

        public virtual Entity<T> GetSingle<T2>() where T2 : T, ISingleton
        {
            var type = typeof (T2);

            // TODO: Create custom Exception type!
            if (!_singletons.ContainsKey(type))
                throw new Exception("Unknown type of single component. Be sure are you using right pool " + type);
            
            var group = _singletons[type];
            return group.GetSingleEntity();
        }

        public bool Is<T2>() where T2 : T, ISingleton, IFlag
        {
            return GetSingle<T2>() != null;
        }

        public bool Toggle<T2>() where T2 : T, ISingleton, IFlag, new()
        { 
            var current = Is<T2>();
            return Toggle<T2>(!current);
        }

        public bool Toggle<T2>(bool flag ) where T2: T, ISingleton, IFlag, new()
        {
            var current = Is<T2>();
            if (current != flag)
            {
                if (flag) CreateEntity().Add<T2>();
                else DestroyEntity(GetSingle<T2>());
            }

            return flag;

        }

        public virtual ISystem CreateSystem(ISystem system)
        {
            var reactiveSystem = system as IReactiveSystem<T>;
            if (reactiveSystem != null)
            {
                return new ReactiveSystem<T>(this, reactiveSystem);
            }
            var multiReactiveSystem = system as IMultiReactiveSystem<T>;
            if (multiReactiveSystem != null)
            {
                return new ReactiveSystem<T>(this, multiReactiveSystem);
            }
            var groupObserverSystem = system as IGroupObserverSystem<T>;
            if (groupObserverSystem != null)
            {
                return new ReactiveSystem<T>(groupObserverSystem);
            }

            return system;
        }

        /// Creates a new entity or gets a reusable entity from the internal ObjectPool for entities.
        public virtual Entity<T> CreateEntity()
        {
            var entity = _reusableEntities.Count > 0 ? _reusableEntities.Pop() : new Entity<T>(_componentPools, _metaData);
            entity._isEnabled = true;
            entity._creationIndex = _creationIndex++;
            entity.Retain(this);
            _entities.Add(entity);
            _entitiesCache = null;

            entity.OnComponentAdded += _cachedUpdateGroupsComponentAddedOrRemoved;
            entity.OnComponentRemoved += _cachedUpdateGroupsComponentAddedOrRemoved;
            entity.OnComponentReplaced += _cachedUpdateGroupsComponentReplaced;
            entity.OnEntityReleased += _cachedOnEntityReleased;

            OnEntityCreated?.Invoke(this, entity);

            return entity;
        }

        /// Returns all entities which are currently in the pool.
        public virtual Entity<T>[] GetEntities()
        {
            if (_entitiesCache == null)
            {
                _entitiesCache = new Entity<T>[_entities.Count];
                _entities.CopyTo(_entitiesCache);
            }

            return _entitiesCache;
        }

        /// Destroys the entity, removes all its components and pushs it back to the internal ObjectPool for entities.
        public virtual void DestroyEntity(Entity<T> entity)
        {
            var removed = _entities.Remove(entity);
            if (!removed)
            {
                throw new PoolDoesNotContainEntityException<T>("'" + this + "' cannot destroy " + entity + "!",
                    "Did you call pool.DestroyEntity() on a wrong pool?");
            }
            _entitiesCache = null;

            OnEntityWillBeDestroyed?.Invoke(this, entity);
            entity.Destroy();
            OnEntityDestroyed?.Invoke(this, entity);

            if (entity.owners.Count == 1)
            {
                // Can be released immediately without going to _retainedEntities
                entity.OnEntityReleased -= _cachedOnEntityReleased;
                _reusableEntities.Push(entity);
                entity.Release(this);
                entity.RemoveAllOnEntityReleasedHandlers();
            }
            else
            {
                _retainedEntities.Add(entity);
                entity.Release(this);
            }
        }

        public Group<T> GetGroup(IMatcher matcher)
        {
            Group<T> group;
            if (!_groups.TryGetValue(matcher, out group))
            {
                group = new Group<T>(matcher);
                var entities = GetEntities();
                for (int i = 0; i < entities.Length; i++)
                {
                    group.HandleEntitySilently(entities[i]);
                }
                _groups.Add(matcher, group);

                for (int i = 0; i < matcher.Types.Length; i++)
                {
                    var type = matcher.Types[i];
                    if (_groupsForTypes[type] == null)
                    {
                        _groupsForTypes[type] = new List<Group<T>>();
                    }
                    _groupsForTypes[type].Add(group);
                }

                OnGroupCreated?.Invoke(this, @group);
            }

            return group;
        }

        /// Destroys all entities in the pool.
        /// Throws an exception if there are still retained entities.
        public virtual void DestroyAllEntities()
        {
            var entities = GetEntities();
            for (int i = 0; i < entities.Length; i++)
            {
                DestroyEntity(entities[i]);
            }

            _entities.Clear();

            if (_retainedEntities.Count != 0)
            {
                throw new PoolStillHasRetainedEntitiesException<T>(this);
            }
        }

        /// Determines whether the pool has the specified entity.
        public virtual bool HasEntity(Entity<T> entity)
        {
            return _entities.Contains(entity);
        }

        /// Clears all groups. This is useful when you want to soft-restart your application.
        public void ClearGroups()
        {
            foreach (var group in _groups.Values)
            {
                group.RemoveAllEventHandlers();
                var entities = group.GetEntities();
                for (int i = 0; i < entities.Length; i++)
                {
                    entities[i].Release(group);
                }

                OnGroupCleared?.Invoke(this, @group);
            }

            _groups.Clear();

            foreach (var type in _metaData.ComponentTypes)
            {
                _groupsForTypes[type] = null;
            }
        }

        /// Resets the creationIndex back to 0.
        public void ResetCreationIndex()
        {
            _creationIndex = 0;
        }

        /// Resets the pool (clears all groups, destroys all entities and resets creationIndex back to 0).
        public void Reset()
        {
            ClearGroups();
            DestroyAllEntities();
            ResetCreationIndex();

            OnEntityCreated = null;
            OnEntityWillBeDestroyed = null;
            OnEntityDestroyed = null;
            OnGroupCreated = null;
            OnGroupCleared = null;
        }
    }
}