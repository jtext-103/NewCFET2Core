using System;
using System.Reflection;
using System.Text.RegularExpressions;
using Jtext103.CFET2.Core.Event;
using Jtext103.CFET2.Core.Exception;
using Jtext103.CFET2.Core.Extension;
using Jtext103.CFET2.Core.Resource;
using Jtext103.CFET2.Core.Sample;

namespace Jtext103.CFET2.Core
{
    public partial class Hub
    {
        private ISample ReadSubscriptionInitial(ResourceBase resource, object[] inputs)
        {
            resource.Info.Implementations.TryGetValue(AccessAction.get, out var getter);
            var method = getter as MethodInfo;
            var parameters = method != null ? method.GetParameters() :
                (getter as PropertyInfo)?.GetIndexParameters() ?? new ParameterInfo[0];
            try
            {
                var mappedInputs = method == null ? inputs : inputs.MapInputDictinary(method);
                bool variadic = parameters.Length > 0 && parameters[parameters.Length - 1].IsDefined(typeof(ParamArrayAttribute), false);
                if (!variadic && mappedInputs.Length > parameters.Length)
                    throw new ArgumentException("Too many getter arguments.");
                // Validate using the existing conversion rules without executing the getter.
                mappedInputs.MapInputParameters(parameters);
            }
            catch (System.Exception ex)
            {
                throw new BadResourceRequestException("SubscribeResource initial get failed: " + resource.Path + ". " + ex.Message);
            }
            return resource.Get(inputs) as ISample;
        }

        private EventFilter CreateExactSubscriptionFilter(string resourceUri, Uri uri, string eventType)
        {
            if (!IsRemoteUri(uri)) return new EventFilter(Regex.Escape(uri.IsAbsoluteUri ? uri.PathAndQuery : resourceUri), eventType);
            var host = myMaster.MyEventHub.GetFullHost(uri.Scheme, uri.Host, uri.Port);
            return new EventFilter(Regex.Escape(uri.PathAndQuery), eventType, 0, host);
        }
    }
}
