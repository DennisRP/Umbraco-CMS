using System;
using System.Collections.Generic;
using System.Linq;
using Umbraco.Core.Collections;
using Umbraco.Core.Models.EntityBase;

namespace Umbraco.Core.Cache
{
    /// <summary>
    /// Represents a caching policy that caches the entire entities set as a single collection.
    /// </summary>
    /// <typeparam name="TEntity">The type of the entity.</typeparam>
    /// <typeparam name="TId">The type of the identifier.</typeparam>
    /// <remarks>
    /// <para>Caches the entire set of entities as a single collection.</para>
    /// <para>Used by Content-, Media- and MemberTypeRepository, DataTypeRepository, DomainRepository,
    /// LanguageRepository, PublicAccessRepository, TemplateRepository... things that make sense to
    /// keep as a whole in memory.</para>
    /// </remarks>
    internal class FullDataSetRepositoryCachePolicy<TEntity, TId> : RepositoryCachePolicyBase<TEntity, TId>
        where TEntity : class, IAggregateRoot
    {
        private readonly Func<TEntity, TId> _entityGetId;
        private readonly Func<IEnumerable<TEntity>> _repoGetAll;
        private readonly bool _expires;

        public FullDataSetRepositoryCachePolicy(IRuntimeCacheProvider cache, Func<TEntity, TId> entityGetId, Func<IEnumerable<TEntity>> repoGetAll, bool expires)
            : base(cache)
        {
            _entityGetId = entityGetId;
            _repoGetAll = repoGetAll;
            _expires = expires;
        }

        protected string GetEntityTypeCacheKey()
        {
            return $"uRepo_{typeof (TEntity).Name}_";
        }

        private void SetCacheActionToClearAll()
        {
            SetCacheAction(() =>
            {
                // clear all, force reload
                Cache.ClearCacheItem(GetEntityTypeCacheKey());
            });
        }

        protected void SetCacheActionToInsertEntities(TEntity[] entities)
        {
            SetCacheAction(() =>
            {
                // cache is expected to be a deep-cloning cache ie it deep-clones whatever is
                // IDeepCloneable when it goes in, and out. it also resets dirty properties,
                // making sure that no 'dirty' entity is cached.
                //
                // this policy is caching the entire list of entities. to ensure that entities
                // are properly deep-clones when cached, it uses a DeepCloneableList. however,
                // we don't want to deep-clone *each* entity in the list when fetching it from
                // cache as that would not be efficient for Get(id). so the DeepCloneableList is
                // set to ListCloneBehavior.CloneOnce ie it will clone *once* when inserting,
                // and then will *not* clone when retrieving.

                if (_expires)
                {
                    Cache.InsertCacheItem(GetEntityTypeCacheKey(), () => new DeepCloneableList<TEntity>(entities), TimeSpan.FromMinutes(5), true);
                }
                else
                {
                    Cache.InsertCacheItem(GetEntityTypeCacheKey(), () => new DeepCloneableList<TEntity>(entities));
                }
            });
        }

        /// <inheritdoc />
        public override void CreateOrUpdate(TEntity entity, Action<TEntity> repoCreateOrUpdate)
        {
            if (entity == null) throw new ArgumentNullException(nameof(entity));
            if (repoCreateOrUpdate == null) throw new ArgumentNullException(nameof(repoCreateOrUpdate));

            try
            {
                repoCreateOrUpdate(entity);
            }
            finally
            {
                SetCacheActionToClearAll();
            }
        }

        /// <inheritdoc />
        public override void Remove(TEntity entity, Action<TEntity> repoRemove)
        {
            if (entity == null) throw new ArgumentNullException(nameof(entity));
            if (repoRemove == null) throw new ArgumentNullException(nameof(repoRemove));

            try
            {
                repoRemove(entity);
            }
            finally
            {
                SetCacheActionToClearAll();
            }
        }

        /// <inheritdoc />
        public override TEntity Get(TId id, Func<TId, TEntity> repoGet)
        {
            return Get(id);
        }

        /// <inheritdoc />
        public override TEntity Get(TId id)
        {
            // get all from the cache, the look for the entity
            var all = GetAllCached();
            var entity = all.FirstOrDefault(x => _entityGetId(x).Equals(id));

            // see note in SetCacheActionToInsertEntities - what we get here is the original
            // cached entity, not a clone, so we need to manually ensure it is deep-cloned.
            return (TEntity) entity?.DeepClone();
        }

        /// <inheritdoc />
        public override bool Exists(TId id, Func<TId, bool> repoExists)
        {
            // get all as one set, then look for the entity
            var all = GetAllCached();
            return all.Any(x => _entityGetId(x).Equals(id));
        }

        /// <inheritdoc />
        public override TEntity[] GetAll(TId[] ids, Func<TId[], IEnumerable<TEntity>> repoGet)
        {
            // get all as one set, from cache if possible, else repo
            var all = GetAllCached();

            // if ids have been specified, filter
            if (ids.Length > 0) all = all.Where(x => ids.Contains(_entityGetId(x)));

            // and return
            // see note in SetCacheActionToInsertEntities - what we get here is the original
            // cached entities, not clones, so we need to manually ensure they are deep-cloned.
            return all.Select(x => (TEntity) x.DeepClone()).ToArray();
        }

        // does NOT clone anything, so be nice with the returned values
        private IEnumerable<TEntity> GetAllCached()
        {
            // try the cache first
            var all = Cache.GetCacheItem<DeepCloneableList<TEntity>>(GetEntityTypeCacheKey());
            if (all != null) return all.ToArray();

            // else get from repo and cache
            var entities = _repoGetAll().WhereNotNull().ToArray();
            SetCacheActionToInsertEntities(entities); // may be an empty array...
            return entities;
        }
    }
}