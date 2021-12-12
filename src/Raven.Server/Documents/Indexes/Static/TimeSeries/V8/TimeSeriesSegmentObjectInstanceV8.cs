using System;
using System.Linq;
using System.Collections.Generic;
using V8.Net;
using Raven.Client.Documents.Indexes.TimeSeries;
using Raven.Server.Documents.Indexes.Static.JavaScript.V8;
using Raven.Server.Documents.Patch;
using Raven.Server.Documents.Patch.V8;

namespace Raven.Server.Documents.Indexes.Static.TimeSeries.V8
{
    public class TimeSeriesSegmentObjectInstanceV8 : ObjectInstanceBaseV8
    {
        private readonly DynamicTimeSeriesSegment _segment;

        public DynamicTimeSeriesSegment Segment
        {
            get {return _segment;}
        }

        public TimeSeriesSegmentObjectInstanceV8(V8EngineEx engineEx, DynamicTimeSeriesSegment segment) 
            : base(engineEx, false)
        {
            _segment = segment ?? throw new ArgumentNullException(nameof(segment));
        }

        public override InternalHandle CreateObjectBinder(bool keepAlive = false)
        {
            return _engine.CreateObjectBinder<TimeSeriesSegmentObjectInstanceV8.CustomBinder>(this, EngineEx.TypeBinderTimeSeriesSegmentObjectInstance, keepAlive: keepAlive);
        }

        public override InternalHandle NamedPropertyGetterOnce(V8EngineEx engineEx, ref string propertyName)
        {
            var engine = (V8Engine)engineEx;
            if (propertyName == nameof(DynamicTimeSeriesSegment.Entries))
            {
                var jsItems = new InternalHandle[_segment._segmentEntry.Segment.NumberOfLiveEntries];
                var i = 0;
                foreach (DynamicTimeSeriesSegment.DynamicTimeSeriesEntry entry in _segment.Entries)
                {
                    jsItems[i] = ((V8EngineEx)engine).CreateObjectBinder<DynamicTimeSeriesEntryCustomBinder>(entry);
                    i++;
                }
                return ((V8EngineEx)engine).CreateArrayWithDisposal(jsItems);
            }

            if (propertyName == nameof(TimeSeriesSegment.DocumentId))
                return engine.CreateValue(_segment._segmentEntry.DocId.ToString());

            if (propertyName == nameof(DynamicTimeSeriesSegment.Name))
                return engine.CreateValue(_segment._segmentEntry.Name.ToString());

            if (propertyName == nameof(DynamicTimeSeriesSegment.Count))
                return engine.CreateValue(_segment.Count);

            if (propertyName == nameof(DynamicTimeSeriesSegment.End))
                return engine.CreateValue(_segment.End);

            if (propertyName == nameof(DynamicTimeSeriesSegment.Start))
                return engine.CreateValue(_segment.Start);

            return InternalHandle.Empty;
        }

        public class CustomBinder : CustomBinder<TimeSeriesSegmentObjectInstanceV8>
        {
        }
    }

    public class DynamicTimeSeriesEntryCustomBinder : ObjectBinderEx<DynamicTimeSeriesSegment.DynamicTimeSeriesEntry>
    {
        public override InternalHandle NamedPropertyGetter(ref string propertyName)
        {
            var timeSeriesEntry = Object as DynamicTimeSeriesSegment.DynamicTimeSeriesEntry;

            if (propertyName == nameof(timeSeriesEntry.Tag))
                return Engine.CreateValue(timeSeriesEntry._entry.Tag?.ToString());
            if (propertyName == nameof(timeSeriesEntry.Timestamp))
                return Engine.CreateValue(timeSeriesEntry._entry.Timestamp);
            if (propertyName == nameof(timeSeriesEntry.Value))
                return Engine.CreateValue(timeSeriesEntry._entry.Values.Span[0]);


            if (propertyName == nameof(timeSeriesEntry.Values))
            {
                int arrayLength =  timeSeriesEntry._entry.Values.Length;
                var jsItems = new InternalHandle[arrayLength];
                for (int i = 0; i < arrayLength; ++i)
                {
                    jsItems[i] = Engine.CreateValue(timeSeriesEntry._entry.Values.Span[i]);
                }

                return ((V8EngineEx)Engine).CreateArrayWithDisposal(jsItems);
            }

            return InternalHandle.Empty;
        }

        public override V8PropertyAttributes? NamedPropertyQuery(ref string propertyName)
        {
            string[] propertyNames = { nameof(ObjClr.Tag), nameof(ObjClr.Timestamp), nameof(ObjClr.Value), nameof(ObjClr.Values) };
            if (Array.IndexOf(propertyNames, propertyName) > -1)
            {
                return V8PropertyAttributes.Locked;
            }
            return null;
        }

        public override InternalHandle NamedPropertyEnumerator()
        {
            string[] propertyNames = { nameof(ObjClr.Tag), nameof(ObjClr.Timestamp), nameof(ObjClr.Value), nameof(ObjClr.Values) };

            int arrayLength =  propertyNames.Length;
            var jsItems = new InternalHandle[arrayLength];
            for (int i = 0; i < arrayLength; ++i)
            {
                jsItems[i] = Engine.CreateValue(propertyNames[i]);
            }
            return ((V8EngineEx)Engine).CreateArrayWithDisposal(jsItems);
        }
    }
}
