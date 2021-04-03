using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LamarCodeGeneration;
using Marten.Events.Aggregation;
using Marten.Exceptions;
using Marten.Schema;
using Marten.Storage;
#nullable enable
namespace Marten.Events.Projections
{
    /// <summary>
    /// Project a single document view across events that may span across
    /// event streams in a user-defined grouping
    /// </summary>
    /// <typeparam name="TDoc"></typeparam>
    /// <typeparam name="TId"></typeparam>
    public abstract class ViewProjection<TDoc, TId>: AggregateProjection<TDoc>, IEventSlicer<TDoc, TId>
    {
        private readonly IList<IGrouper<TId>> _groupers = new List<IGrouper<TId>>();
        private readonly List<IFanOutRule> _fanouts = new();
        private IEventSlicer<TDoc, TId> _eventSlicer;

        protected ViewProjection()
        {
            Lifecycle = ProjectionLifecycle.Async;
        }

        protected override Type[] determineEventTypes()
        {
            return base.determineEventTypes().Concat(_fanouts.Select(x => x.OriginatingType))
                .Distinct().ToArray();
        }

        public void Identity<TEvent>(Func<TEvent, TId> identityFunc)
        {
            var grouper = new Grouper<TId, TEvent>(identityFunc);
            _groupers.Add(grouper);
        }

        public void EventSlicer(IEventSlicer<TDoc, TId> eventSlicer) => _eventSlicer = eventSlicer;

        protected override void specialAssertValid()
        {
            if (!_groupers.Any())
            {
                throw new InvalidProjectionException(
                    $"ViewProjection {GetType().FullNameInCode()} has no Identity() rules defined and does not know how to identify event membership in the aggregated document {typeof(TDoc).FullNameInCode()}");
            }
        }

        public void FanOut<TEvent, TChild>(Func<TEvent, IEnumerable<TChild>> fanOutFunc)
        {
            var fanout = new FanOutOperator<TEvent, TChild>(fanOutFunc);
            _fanouts.Add(fanout);
        }

        ValueTask<IReadOnlyList<EventSlice<TDoc, TId>>> IEventSlicer<TDoc, TId>.Slice(IQuerySession querySession,
            IEnumerable<StreamAction> streams, ITenancy tenancy)
        {
            return _eventSlicer?.Slice(querySession, streams, tenancy) ??
                   new ValueTask<IReadOnlyList<EventSlice<TDoc, TId>>>(Slice(streams, tenancy).ToList());
        }

        ValueTask<IReadOnlyList<TenantSliceGroup<TDoc, TId>>> IEventSlicer<TDoc, TId>.Slice(IQuerySession querySession,
            IReadOnlyList<IEvent> events, ITenancy tenancy)
        {
            if (_eventSlicer != null)
                return _eventSlicer.Slice(querySession, events, tenancy);

            var tenantGroups = events.GroupBy(x => x.TenantId);
            var slices = tenantGroups.Select(x => Slice(tenancy[x.Key], x.ToList())).ToList();
            return new ValueTask<IReadOnlyList<TenantSliceGroup<TDoc, TId>>>(slices);
        }


        internal IEnumerable<EventSlice<TDoc, TId>> Slice(IEnumerable<StreamAction> streams, ITenancy tenancy)
        {
            var events = streams.SelectMany(x => x.Events);
            var tenantGroups = events.GroupBy(x => x.TenantId);
            foreach (var @group in tenantGroups)
            {
                var tenant = tenancy[@group.Key];
                foreach (var slice in Slice(tenant, @group.ToArray()).Slices)
                {
                    yield return slice;
                }
            }
        }

        internal TenantSliceGroup<TDoc, TId> Slice(ITenant tenant, IList<IEvent> events)
        {
            var grouping = new EventGrouping<TId>();
            foreach (var grouper in _groupers)
            {
                grouper.Group(events, grouping);
            }

            return grouping.BuildSlices<TDoc>(tenant, _fanouts);
        }

        protected override object buildEventSlicer()
        {
            return this;
        }

        protected override IEnumerable<string> validateDocumentIdentity(StoreOptions options, DocumentMapping mapping)
        {
            yield break;
        }
    }
}
