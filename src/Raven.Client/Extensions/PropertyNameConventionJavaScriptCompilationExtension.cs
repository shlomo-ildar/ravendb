﻿using System.Reflection;
using Lambda2Js;
using Raven.Client.Documents.Conventions;

namespace Raven.Client.Extensions
{
    internal class PropertyNameConventionJSMetadataProvider : JavascriptMetadataProvider
    {
        private readonly DocumentConventions _conventions;
        public bool UseOptionalChanining;
        public string OptionalChanining;

        public PropertyNameConventionJSMetadataProvider(DocumentConventions conventions, bool useOptionalChanining = false)
        {
            _conventions = conventions;
            UseOptionalChanining = useOptionalChanining;
            OptionalChanining = UseOptionalChanining ? "?" : "";
        }

        public class MemberMetadata : IJavascriptMemberMetadata
        {
            public string MemberName { get; set; }
        }

        public override IJavascriptMemberMetadata GetMemberMetadata(MemberInfo memberInfo)
        {
            string name = null;
            if (_conventions.PropertyNameConverter != null)
            {
                if (memberInfo.DeclaringType?.Namespace?.StartsWith("System") == false &&
                    memberInfo.DeclaringType?.Namespace?.StartsWith("Microsoft") == false)
                {
                    name = _conventions.PropertyNameConverter(memberInfo);
                }              
            }

            return string.IsNullOrWhiteSpace(name) ?
                new MemberMetadata { MemberName = memberInfo.Name } :
                new MemberMetadata { MemberName = name };
        }
    }
}
