using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Jtext103.CFET2.Core.Resource;
using Jtext103.CFET2.Core.Sample;
using Jtext103.CFET2.Core.Exception;
using System.Net.Http;
using Jtext103.CFET2.Core.Extension;
using Jtext103.CFET2.Core.Communication;
using Jtext103.CFET2.Core.Event;
using Jtext103.CFET2.Core.Middleware;
using Jtext103.CFET2.Core.Log;

namespace Jtext103.CFET2.Core
{
    /// <summary>
    /// a proxy object for HubMaster, every cfet2 module has one to consume cfet2 service
    /// </summary>
    public partial class Hub
    {
        public ICfet2Logger logger = Cfet2LogManager.GetLogger("Hub");
        internal Hub(CFET2Module module)
        {
            //todo depending on the module types disable some function of the hub
            MyCfet2Module = module;
            publicationMaster = HubMaster.getInstance();
        }


        #region props

        /// <summary>
        /// all thing stored in a flat structure, will become obsolete when hierarchy is done
        /// </summary>
        //public Dictionary<string, ResourceBase> Resources { get; } = new Dictionary<string, ResourceBase>();


        private HubMaster myMaster
        {
            get
            {
                return HubMaster.getInstance();
            }
        }


        /// <summary>
        /// access the master's pipeline
        /// </summary>
        public Pipeline Pipeline
        {
            get
            {
                return myMaster.MyPipeline;
            }
        }

        /// <summary>
        /// the cfet module that holds this hub
        /// </summary>
        public CFET2Module MyCfet2Module { get; private set; }

        /// <summary>
        /// this is a reference to the MasterHub's Event hub
        /// </summary>
        public EventHub EventHub
        {
            get
            {
                return myMaster.MyEventHub;
            }
        }

        internal TimeSpan ResourceObservationInterval
        {
            get
            {
                return myMaster.MyEventHub.ResourceObservationInterval;
            }
            set
            {
                myMaster.MyEventHub.ResourceObservationInterval = value;
            }
        }

        internal int ObservedResourceCount
        {
            get
            {
                return myMaster.MyEventHub.ObservedResourceCount;
            }
        }

        #endregion

       

        /// <summary>
        /// get all resources
        /// </summary>
        /// <returns></returns>
        public Dictionary<string, ResourceBase> GetAllLocalResources()
        {
            return myMaster.Resources;
        }

        #region local get set invoke

        /// <summary>
        /// get an resource objet of provided resource path,
        /// it could be thing, status, config,  and so on
        /// </summary>
        /// <param name="resourcePath">the path of the thing, in unix file path fasion</param>
        /// <returns>will return null it the path points to nothing</returns>
        public ResourceBase GetLocalResouce(string resourcePath)
        {
            if (myMaster.Resources.ContainsKey(resourcePath))
            {
                return myMaster.Resources[resourcePath];
            }
            return null;
        }


        /*get*/
        /// <summary>
        /// get an sample of that resource objet of provided resource uri, the uri must be a resource locator and contains no parameters
        /// it could be thing, status, config,  and so on, will thrown is the resource not found, give invalid sample
        /// </summary>
        /// <param name="resourcePath">the path to a locol resource, there is not input in it, todo: 1 support regex, 2. support relative path, easy</param>
        /// <param name="inputs">input parameter in array</param>
        /// <returns></returns>
        public ISample TryGetLocalResouceSample(string resourcePath, params object[] inputs)
        {
            var resource = GetLocalResouce(resourcePath);
            if (resource != null)
            {
                return resource.Get(inputs) as ISample;
            }
            throw new ResourceDoesNotExistException();
        }

        /// <summary>
        /// get an sample of that resource objet of provided resource uri, the uri must be a resource locator and contains no parameters
        /// it could be thing, status, config,  and so on, will thrown is the resource not found, give invalid sample
        /// </summary>
        /// <param name="resourcePath">the path to a locol resource, there is not input in it</param>
        /// <param name="inputDict">input parameter in dictionary</param>
        /// <returns></returns>
        public ISample TryGetLocalResouceSample(string resourcePath, Dictionary<string,object> inputDict)
        {
            var resource = GetLocalResouce(resourcePath);
            if (resource != null)
            {
                //this is almost the same as array but only add an indicator to indicate thie input has a dictionary
                return resource.Get(inputDict) as ISample;
            }
            throw new ResourceDoesNotExistException();
        }

        /*set*/

        /// <summary>
        /// only config resource support set
        /// </summary>
        /// <param name="resourcePath">the path to a locol resource, there is not input in it, todo: 1 support regex, 2. support relative path, easy</param>
        /// <param name="inputs">input parameter in array</param>
        /// <returns></returns>
        public ISample TrySetLocalResouceSample(string resourcePath, params object[] inputs)
        {
            var resource = GetLocalResouce(resourcePath);
            if (resource != null)
            {
                if (resource.ResourceType == ResourceTypes.Config)
                {
                    return (resource as ResourceConfig).Set(inputs) as ISample;
                }
                throw new WrongResourceActionException();
            }
            throw new ResourceDoesNotExistException();
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="resourcePath">the path to a locol resource, there is not input in it</param>
        /// <param name="inputDict">input parameter in dictionary</param>
        /// <returns></returns>
        public ISample TrySetLocalResouceSample(string resourcePath, Dictionary<string, object> inputDict)
        {
            var resource = GetLocalResouce(resourcePath);
            if (resource != null)
            {
                if (resource.ResourceType == ResourceTypes.Config)
                {
                    return (resource as ResourceConfig).Set(inputDict) as ISample;
                }
                throw new WrongResourceActionException();
            }
            throw new ResourceDoesNotExistException();
        }

        /*invoke*/
        /// <summary>
        /// only method resource support set
        /// </summary>
        /// <param name="resourcePath">the path to a locol resource, there is not input in it, todo: 1 support regex, 2. support relative path, easy</param>
        /// <param name="inputs">input parameter in array</param>
        /// <returns></returns>
        public ISample TryInvokeLocalResouceSample(string resourcePath, params object[] inputs)
        {
            var resource = GetLocalResouce(resourcePath);
            if (resource != null)
            {
                if (resource.ResourceType == ResourceTypes.Method)
                {
                    return (resource as ResourceMethod).Invoke(inputs) as ISample;
                }
                throw new WrongResourceActionException();
            }
            throw new ResourceDoesNotExistException();
        }

        /// <summary>
        /// invole a resource, localy, meaning no communicatin module will be invoked
        /// </summary>
        /// <param name="resourcePath">the path to a locol resource, there is not input in it</param>
        /// <param name="inputDict">input parameter in dictionary</param>
        /// <returns>result sample</returns>
        public ISample TryInvokeLocalResouceSample(string resourcePath, Dictionary<string, object> inputDict)
        {
            var resource = GetLocalResouce(resourcePath);
            if (resource != null)
            {
                if (resource.ResourceType == ResourceTypes.Method)
                {
                    return (resource as ResourceMethod).Invoke(inputDict) as ISample;
                }
                throw new WrongResourceActionException();
            }
            throw new ResourceDoesNotExistException();
        }



        #endregion

        #region subscribe event

        /// <summary>
        /// Subscribe to a normal event. Resource-level subscriptions should use SubscribeResource.
        /// </summary>
        public Token Subscribe(EventFilter filter, Action<EventArg> handler)
        {
#pragma warning disable CS0612
            return myMaster.MyEventHub.Subscribe(filter, handler);
#pragma warning restore CS0612
        }

        /// <summary>
        /// Unsubscribe from a normal event subscription.
        /// </summary>
        public void Unsubscribe(Token token)
        {
#pragma warning disable CS0612
            myMaster.MyEventHub.Unsubscribe(token);
#pragma warning restore CS0612
        }

        /// <summary>
        /// Publish a normal local event. The payload will be wrapped in a status sample if it is not already a sample.
        /// </summary>
        public void Publish(string source, string eventType, object payload)
        {
#pragma warning disable CS0612
            myMaster.MyEventHub.Publish(source, eventType, payload);
#pragma warning restore CS0612
        }

        /// <summary>
        /// Publish a normal local event.
        /// </summary>
        public void Publish(EventArg eventArg)
        {
#pragma warning disable CS0612
            myMaster.MyEventHub.Publish(eventArg);
#pragma warning restore CS0612
        }

        /// <summary>
        /// Publish a changed event for a resource.
        /// </summary>
        public void PublishResource(string resourcePath, object value)
        {
            Publish(resourcePath, "changed", value);
        }

        #endregion

        #region subscribe resource

        public ResourceSubscription SubscribeResource(string resourceUri, Action<EventArg> handler, string eventType = "changed")
        {
            var uri = new Uri(resourceUri, UriKind.RelativeOrAbsolute);
            if (IsRemoteUri(uri))
            {
                return subscribeRemoteResource(resourceUri, uri, handler, eventType);
            }

            if (!resourceUri.StartsWith("/", StringComparison.Ordinal) && (!uri.IsAbsoluteUri || uri.IsFile))
            {
                throw new ArgumentException("Local subscription paths must start with '/'.", nameof(resourceUri));
            }

            var parsed = ParseLocalRequest(uri);
            var resource = GetLocalResouce(parsed.ResourcePath);
            if (resource == null)
            {
                throw new ResourceDoesNotExistException();
            }

            if (resource.ResourceType != ResourceTypes.Status && resource.ResourceType != ResourceTypes.Config)
            {
                throw new WrongResourceActionException("SubscribeResource only supports Status and Config resources: " + resource.Path);
            }

            bool needsObservation = !resource.SupportsNativeEvent && !resource.Info.HasGetterParameters;
            if (needsObservation && !string.Equals(eventType, "changed", StringComparison.InvariantCultureIgnoreCase))
            {
                throw new WrongResourceActionException("Observed resources without Cfet2Event only support changed event: " + resource.Path);
            }

            if (needsObservation)
            {
                ResourceObservationTypePolicy.EnsureSupported(resource);
            }

            var initialValue = SlowResourceReadMonitor.Measure(resource.Path, "initial", () =>
                localAccessWithUri((path, args) => ReadSubscriptionInitial(resource, args),
                    (path, dict) => ReadSubscriptionInitial(resource, new object[] { new DictionaryInputsIndicator(), dict }),
                    resourceUri, null, null, false));
            ensureValidSubscribeInitialValue(initialValue, resource.Path);

            var filter = CreateExactSubscriptionFilter(resourceUri, uri, eventType);
            var token = Subscribe(filter, handler);
            try
            {
                if (needsObservation)
                {
                    myMaster.MyEventHub.ObserveResource(token, resource, initialValue);
                }
                return new ResourceSubscription(token, initialValue);
            }
            catch
            {
                Unsubscribe(token);
                throw;
            }
        }

        private ResourceSubscription subscribeRemoteResource(string resourceUri, Uri uri, Action<EventArg> handler, string eventType)
        {
            if (myMaster.MyCommunicationManager.GetModuleFor(uri.Scheme) == null)
            {
                throw new ProtocolNotSuportedException($"{uri.Scheme} is not supported");
            }

            if (myMaster.MyCommunicationManager.GetModuleForEvent(uri.Scheme) == null)
            {
                throw new ProtocolNotSuportedException($"{uri.Scheme} event is not supported");
            }

            var initialValue = TryGetResourceSampleWithUri(resourceUri);
            ensureValidSubscribeInitialValue(initialValue, resourceUri);
            if (initialValue.ResourceType != ResourceTypes.Status && initialValue.ResourceType != ResourceTypes.Config)
            {
                throw new WrongResourceActionException("SubscribeResource only supports Status and Config resources: " + resourceUri);
            }

            var filter = CreateExactSubscriptionFilter(resourceUri, uri, eventType);
            var token = Subscribe(filter, handler);
            if (!myMaster.MyEventHub.IsSubscribed(token))
            {
                Unsubscribe(token);
                throw new BadResourceRequestException("SubscribeResource event subscribe failed: " + resourceUri);
            }

            return new ResourceSubscription(token, initialValue);
        }

        private static void ensureValidSubscribeInitialValue(ISample initialValue, string resourcePath)
        {
            if (initialValue == null || !initialValue.IsValid)
            {
                throw new BadResourceRequestException("SubscribeResource initial get failed: " + resourcePath);
            }
        }

        public ResourceSubscription TrySubscribeResourceWithUri(string resourceUri, string eventType, Action<EventArg> handler)
        {
            return SubscribeResource(resourceUri, handler, eventType);
        }

        #endregion



        #region get set invoke using uri 
        /// <summary>
        /// simple resource get,only supports url with params(if the resource has params).If you want to add params alone,you can try "TryAccessResourceSampleWithUri"
        /// </summary>
        /// <param name="requestUri"></param>
        /// <returns></returns>
        public ISample Get(string requestUri)
        {
            ResourceRequest request = new ResourceRequest(requestUri, AccessAction.get, null, null, null);
            return TryAccessResourceSampleWithUri(request);
        }


        /// <summary>
        /// simple resource set,only supports url with params(if the resource has params).If you want to add params alone,you can try "TryAccessResourceSampleWithUri"
        /// </summary>
        /// <param name="requestUri"></param>
        /// <returns></returns>
        public ISample Set(string requestUri)
        {
            ResourceRequest request = new ResourceRequest(requestUri, AccessAction.set, null, null, null);
            return TryAccessResourceSampleWithUri(request);
        }

        /// <summary>
        /// simple resource Invoke,only supports url with params(if the resource has params).If you want to add params alone,you can try "TryAccessResourceSampleWithUri"
        /// </summary>
        /// <param name="requestUri"></param>
        /// <returns></returns>
        public ISample Invoke(string requestUri)
        {
            ResourceRequest request = new ResourceRequest(requestUri, AccessAction.invoke, null, null, null);
            return TryAccessResourceSampleWithUri(request);
        }


        /*request resource, with pipeline support*/
        /// <summary>
        /// 
        /// </summary>
        /// <param name="request"></param>
        /// <returns></returns>
        public ISample TryAccessResourceSampleWithUri(ResourceRequest request)
        {
            ISample resource = null;
            switch (request.Action)
            {
                case AccessAction.get:
                    if (request.UsingInputDict)
                        resource = TryGetResourceSampleWithUri(request.RequestUri, request.InputDict);
                    else
                        resource = TryGetResourceSampleWithUri(request.RequestUri, request.InputArray);
                    break;
                case AccessAction.set:
                    if (request.UsingInputDict)
                        resource = TrySetResourceSampleWithUri(request.RequestUri, request.InputDict);
                    else
                        resource = TrySetResourceSampleWithUri(request.RequestUri, request.InputArray);
                    break;
                case AccessAction.invoke:
                    if (request.UsingInputDict)
                        resource = TryInvokeSampleResourceWithUri(request.RequestUri, request.InputDict);
                    else
                        resource = TryInvokeSampleResourceWithUri(request.RequestUri, request.InputArray);
                    break;
                default:
                    throw new WrongResourceActionException();
            }
            if (resource.IsRemote)
            {
                return resource;
            }
            return myMaster.MyPipeline.BatchProcess(resource, request);
        }


        private ISample localAccessWithUri(Func<string, object[], ISample> arrayAccessFunc,
            Func<string, Dictionary<string, object>, ISample> dictAccessFunc,
            string requestUri, Dictionary<string, object> inputDict, object[] inputs,bool usingDictInput)
        {
            var uri = new Uri(requestUri, UriKind.RelativeOrAbsolute);
            //if is using dict indicate by setting ParseLocalRequest input = null to ignore the array input
            //the below find the real path to the resource by mathc from the path backward the excessive parts in the path is add to the input
            (var resourcePath, var realInputs, var queryInputDict) = ParseLocalRequest(uri, usingDictInput?null:inputs);

            if (queryInputDict != null || usingDictInput)
            {
                var realInputDict = new Dictionary<string, object>();
                if (queryInputDict != null)
                {
                    queryInputDict.ToList().ForEach(p => realInputDict[p.Key] = p.Value);
                }
                if (inputDict != null) //input dict override the query
                {
                    inputDict.ToList().ForEach(p => realInputDict[p.Key] = p.Value);
                }
                if (realInputs.Length > 0) //if we have querry or dict input we joint route parameter an put it at last
                {
                    var jointRouteInput = "";
                    for (int i = 0; i < realInputs.Length; i++)
                    {
                        jointRouteInput += @"/" + realInputs[i].ToString();
                    }
                    realInputDict.Add(CommonConstants.TheLastInputsKey, jointRouteInput);
                }
                return dictAccessFunc(resourcePath, realInputDict);
            }
            return arrayAccessFunc(resourcePath, realInputs);
        }



        /*get*/

        /// <summary>
        /// get a sample using a uri, the inputs are parameters to the resource, if you are using parameters in uri, then you can 
        /// have only one input, if you have more than one inputs, the uri must points to a resource without any route parameters and query string, 
        /// </summary>
        /// <param name="requestUri">the uri to access a resource, it can contain route parameters or query string parameters</param>
        /// <param name="inputs">if contain more than one inputs, then query string (if any) in uri will be ignored, the path of URI is point to a resource, route parameters will not be recognize</param>
        /// <returns></returns>
        [Obsolete]
        public ISample TryGetResourceSampleWithUri(string requestUri, params object[] inputs)
        {
            var uri = new Uri(requestUri, UriKind.RelativeOrAbsolute);
            if (IsRemoteUri(uri))
            {
                var comm = myMaster.MyCommunicationManager.GetModuleFor(uri.Scheme);
                if (comm != null)
                {
                    ISample result = comm.TryGetResourceSampleWithUri(requestUri, inputs);
                    result.IsRemote = true;
                    return result;
                }
                else
                {
                    throw new ProtocolNotSuportedException($"{uri.Scheme} is not supported");
                }
            }
            else
            {
                return localAccessWithUri(TryGetLocalResouceSample, TryGetLocalResouceSample,
                    requestUri, null, inputs, false);
            }
        }

        /// <summary>
        /// get the resource with dictionary input, using this, you can not use the route parameter, but you can still use query string parameters, the duplicated keys will be overrided by the inputDict 
        /// this is as if you have query string inthe uri but its in the input dict.
        /// </summary>
        /// <param name="requestUri"></param>
        /// <param name="inputDict"></param>
        /// <returns></returns>
        [Obsolete]
        public ISample TryGetResourceSampleWithUri(string requestUri, Dictionary<string,object> inputDict)
        {
            var uri = new Uri(requestUri, UriKind.RelativeOrAbsolute);
            if (IsRemoteUri(uri))
            {
                var comm = myMaster.MyCommunicationManager.GetModuleFor(uri.Scheme);
                if (comm != null)
                {
                    return comm.TryGetResourceSampleWithUri(requestUri, inputDict);
                }
                else
                {
                    throw new ProtocolNotSuportedException($"{uri.Scheme} is not supported");
                }
            }
            else
            {
                return localAccessWithUri(TryGetLocalResouceSample, TryGetLocalResouceSample,
                    requestUri, inputDict, null,  true);
            }
        }


        /*set*/
        /// <summary>
        /// set a sample using a uri, the inputs are parameters to the resource, if you are using parameters in uri, then you can 
        /// have only one input, if you have more than one inputs, the uri must points to a resource without any route parameters and query string, 
        /// </summary>
        /// <param name="requestUri">the uri to access a resource, it can contain route parameters or query string parameters</param>
        /// <param name="inputs">if contain more than one inputs, then query string (if any) in uri will be ignored, the path of URI is point to a resource, route parameters will not be recognize</param>
        /// <returns></returns>
        [Obsolete]
        public ISample TrySetResourceSampleWithUri(string requestUri, params object[] inputs)
        {
            var uri = new Uri(requestUri, UriKind.RelativeOrAbsolute);
            if (IsRemoteUri(uri))
            {
                var comm = myMaster.MyCommunicationManager.GetModuleFor(uri.Scheme);
                if (comm != null)
                {
                    return comm.TrySetResourceSampleWithUri(requestUri, inputs);
                }
                else
                {
                    throw new ProtocolNotSuportedException($"{uri.Scheme} is not supported");
                }
            }
            else
            {
                return localAccessWithUri(TrySetLocalResouceSample, TrySetLocalResouceSample,
                    requestUri,null, inputs, false);
            }
        }

        /// <summary>
        /// set the resource with dictionary input, using this, you can not use the route parameter, but you can still use query string parameters, the duplicated keys will be overrided by the inputDict 
        /// this is as if you have query string inthe uri but its in the input dict.
        /// </summary>
        /// <param name="requestUri"></param>
        /// <param name="inputDict"></param>
        /// <returns></returns>
        [Obsolete]
        public ISample TrySetResourceSampleWithUri(string requestUri, Dictionary<string, object> inputDict)
        {
            var uri = new Uri(requestUri, UriKind.RelativeOrAbsolute);

            if (IsRemoteUri(uri))
            {
                var comm = myMaster.MyCommunicationManager.GetModuleFor(uri.Scheme);
                if (comm != null)
                {
                    return comm.TrySetResourceSampleWithUri(requestUri, inputDict);
                }
                else
                {
                    throw new ProtocolNotSuportedException($"{uri.Scheme} is not supported");
                }
            }
            else
            {
                return localAccessWithUri(TrySetLocalResouceSample, TrySetLocalResouceSample,
                    requestUri, inputDict, null, true);
            }
        }


        /*invoke*/
        /// <summary>
        /// set a sample using a uri, the inputs are parameters to the resource, if you are using parameters in uri, then you can 
        /// have only one input, if you have more than one inputs, the uri must points to a resource without any route parameters and query string, 
        /// </summary>
        /// <param name="requestUri">the uri to access a resource, it can contain route parameters or query string parameters</param>
        /// <param name="inputs">if contain more than one inputs, then query string (if any) in uri will be ignored, the path of URI is point to a resource, route parameters will not be recognize</param>
        /// <returns></returns>
        [Obsolete]
        public ISample TryInvokeSampleResourceWithUri(string requestUri, params object[] inputs)
        {
            var uri = new Uri(requestUri, UriKind.RelativeOrAbsolute);
            if (IsRemoteUri(uri))
            {
                var comm = myMaster.MyCommunicationManager.GetModuleFor(uri.Scheme);
                if (comm != null)
                {
                    return comm.TryInvokeSampleResourceWithUri(requestUri, inputs);
                }
                else
                {
                    throw new ProtocolNotSuportedException($"{uri.Scheme} is not supported");
                }
            }
            else
            {
                return localAccessWithUri(TryInvokeLocalResouceSample, TryInvokeLocalResouceSample,
                    requestUri, null, inputs, false);
            }
        }

        /// <summary>
        /// set the resource with dictionary input, using this, you can not use the route parameter, but you can still use query string parameters, the duplicated keys will be overrided by the inputDict 
        /// this is as if you have query string inthe uri but its in the input dict.
        /// </summary>
        /// <param name="requestUri"></param>
        /// <param name="inputDict"></param>
        /// <returns></returns>
        [Obsolete]
        public ISample TryInvokeSampleResourceWithUri(string requestUri, Dictionary<string, object> inputDict)
        {
            var uri = new Uri(requestUri, UriKind.RelativeOrAbsolute);
            if (IsRemoteUri(uri))
            {
                var comm = myMaster.MyCommunicationManager.GetModuleFor(uri.Scheme);
                if (comm != null)
                {
                    return comm.TryInvokeSampleResourceWithUri(requestUri, inputDict);
                }
                else
                {
                    throw new ProtocolNotSuportedException($"{uri.Scheme} is not supported");
                }
            }
            else
            {
                return localAccessWithUri(TryInvokeLocalResouceSample, TryInvokeLocalResouceSample,
                    requestUri, inputDict, null, true);
            }
        }

        #endregion

    }
}
