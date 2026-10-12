using Jtext103.CFET2.Core.Sample;
using System;
using System.Collections.Generic;
using System.Reflection;

namespace Jtext103.CFET2.Core.Resource
{
    /// <summary>
    /// describe the resource
    /// </summary>
    public class ResourceInfo
    {
        public ResourceTypes Type { get; set; }

        public bool SupportsNativeEvent { get; set; }

        /// <summary>Any declared getter argument requires component-driven events, even if optional.</summary>
        public bool HasGetterParameters
        {
            get
            {
                if (!Implementations.TryGetValue(AccessAction.get, out var getter)) return false;
                return getter is MethodInfo method ? method.GetParameters().Length != 0 :
                    getter is PropertyInfo property && property.GetIndexParameters().Length != 0;
            }
        }

        /// <summary>
        /// the implementions of this resource
        /// </summary>
        public Dictionary<AccessAction, MemberInfo> Implementations = new Dictionary<AccessAction, MemberInfo>();

    }
}
