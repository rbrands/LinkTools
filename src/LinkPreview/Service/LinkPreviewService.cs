﻿//using HttpCompletionOption.ResponseHeadersRead solves quite some problems,
//it all started with nzz.ch, luckily we solved it following this post:
//https://stackoverflow.com/questions/33233780/system-net-http-httprequestexception-error-while-copying-content-to-a-stream
//as I am already checking for the status codes of the response,
//this was a no brainer to add

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace MSiccDev.Libs.LinkTools.LinkPreview
{
    public class LinkPreviewService : ILinkPreviewService
    {
        private static HttpClient _httpClientInstance = null;
        private readonly InternalHttpClient _client;
        private readonly HttpClientConfiguration _configWithCompression;
        private readonly HttpClientConfiguration _configWithOutCompression;


        public LinkPreviewService()
        {
            _client = new InternalHttpClient();
            _configWithCompression = new HttpClientConfiguration()
            {
                AcceptHeader = "text/html",
                CustomUserAgentString = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/84.0.4133.0 Safari/537.36 Edg/84.0.508.0",
                AllowRedirect = false,
                UseCookies = false,
                UseCompression = true,
                Timeout = TimeSpan.FromSeconds(30)
            };

            _configWithOutCompression = new HttpClientConfiguration()
            {
                AcceptHeader = "text/html",
                CustomUserAgentString = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/84.0.4133.0 Safari/537.36 Edg/84.0.508.0",
                AllowRedirect = false,
                UseCookies = false,
                UseCompression = false,
                Timeout = TimeSpan.FromSeconds(30)
            };

            _httpClientInstance = _client.GetStaticClient(_configWithCompression);

        }



        public async Task<LinkPreviewRequest> GetLinkDataAsync(LinkPreviewRequest previewRequest, bool isCircleRedirect = false, bool retryWithoutCompressionOnFailure = true, bool noCompression = false, bool includeDescription = false)
        {
            try
            {
                var currentRequestedUrlString = previewRequest.CurrentRequestedUrl.ToString();

                if (currentRequestedUrlString.Contains("facebook.com") &&
                    previewRequest.CurrentRequestedUrl.ContainsParameter("u"))
                {
                    return await HandleFacebookExitLink(previewRequest);
                }
                //todo: add other special cases (twitch?, youtube?) here...
                else
                {
                    if (!isCircleRedirect)
                    {
                        Console.WriteLine($"sending request for url {previewRequest.CurrentRequestedUrl}");

                        var request = new HttpRequestMessage(currentRequestedUrlString.IsHttps() ? HttpMethod.Get : HttpMethod.Head, previewRequest.CurrentRequestedUrl);
                        request.Headers.Host = previewRequest.CurrentRequestedUrl.Host;

                        HttpCompletionOption completionOption = HttpCompletionOption.ResponseHeadersRead;

                        var response = noCompression ?
                                       await _httpClientInstance.SendAsync(request, completionOption) :
                                       await TryGetResponseMessageWithoutCompressionAsync(request, completionOption);

                        if (request.RequestUri == previewRequest.OriginalUrl)
                            previewRequest.OriginalResponse = response;
                        else
                            previewRequest.Redirects.Add(previewRequest.CurrentRequestedUrl.ToString(), response);

                        var statusCode = (int)response.StatusCode;
                        Console.WriteLine($"received response {statusCode} from url {request.RequestUri}");

                        if (statusCode >= 300 && statusCode <= 399)
                        {
                            return await HandleRedirect(response, previewRequest);
                        }
                        else if (statusCode >= 400)
                        {
                            var message = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                            previewRequest.Error = new RequestError(statusCode, message);

                            Console.WriteLine($"got error response ({statusCode}) from {previewRequest.CurrentRequestedUrl}\nmessage: {message}");
                        }
                        else
                        {
                            var linkPreview = await TryGetLinkPreview(response, includeDescription);
                            previewRequest.Result = linkPreview;
                        }
                    }
                    else
                    {
                        await TryGetLinkDataFrom302Redirects(previewRequest, includeDescription).ConfigureAwait(false);
                    }

                    return previewRequest;
                }
            }
            catch (Exception ex)
            {
                if (ex is HttpRequestException requestException)
                {
                    //socket exceptions get wrapped in http request exceptions
                    //avoiding circular requests by explicitly returning
                    if (ex.InnerException is SocketException socketException)
                    {
                        previewRequest.Error = new RequestError(socketException);
                        return previewRequest;
                    }
                    else
                    {
                        //in many cases, this leads to a success
                        if (retryWithoutCompressionOnFailure)
                        {
                            await GetLinkDataAsync(previewRequest, false, true);
                        }
                    }
                }                  

                previewRequest.Error = new RequestError(ex);

                Console.WriteLine($"{ex.GetType()}:{ex.Message} for url {previewRequest.CurrentRequestedUrl} in {nameof(GetLinkDataAsync)}");
                return previewRequest;
            }

        }


        private async Task<LinkPreviewRequest> HandleFacebookExitLink(LinkPreviewRequest previewRequest)
        {

            var correctLink = previewRequest.CurrentRequestedUrl.TryGetLinkFromFacebookExitLink();

            if (correctLink != null)
                previewRequest.CurrentRequestedUrl = correctLink;

            return await GetLinkDataAsync(previewRequest, false);
        }


        private async Task TryGetLinkDataFrom302Redirects(LinkPreviewRequest previewRequest, bool includeDescription)
        {
            if (previewRequest.OriginalResponse.StatusCode == HttpStatusCode.Found)
            {
                var linkPreview = await TryGetLinkPreview(previewRequest.OriginalResponse, includeDescription);
                previewRequest.Result = linkPreview;
            }
            else if (previewRequest.Redirects.Values.Any(r => r.StatusCode == HttpStatusCode.Found))
            {
                var linkPreviewTasks = new List<Task<LinkInfo>>();
                foreach (var response in previewRequest.Redirects.Values.Where(r => r.StatusCode == HttpStatusCode.Found))
                {
                    linkPreviewTasks.Add(TryGetLinkPreview(response, includeDescription));
                }

                var linkPreviews = await Task.WhenAll(linkPreviewTasks).ConfigureAwait(false);

                var linkWithTitleAndImage = linkPreviews.FirstOrDefault(p => !string.IsNullOrEmpty(p.Title) && p.ImageUrl != null);
                previewRequest.Result = linkWithTitleAndImage ?? linkPreviews.FirstOrDefault(p => !string.IsNullOrEmpty(p.Title));
            }
        }

        private async Task<HttpResponseMessage> TryGetResponseMessageWithoutCompressionAsync(HttpRequestMessage requestMessage, HttpCompletionOption completionOption)
        {
            var tempClient = _client.GetTemporaryClient(_configWithOutCompression);

            var response = await tempClient.SendAsync(requestMessage, completionOption);

            tempClient.Dispose();

            return response;
        }


        private async Task<LinkPreviewRequest> HandleRedirect(HttpResponseMessage response, LinkPreviewRequest previewRequest)
        {
            var redirectUri = response.Headers.Location;

            if (redirectUri != null)
            {
                Console.WriteLine($"got redirect ({response.StatusCode}) from {previewRequest.CurrentRequestedUrl} to {redirectUri} (https: {redirectUri.ToString().IsHttps()})");

                var redirectUriString = redirectUri.ToString();
                if (!redirectUriString.IsHttps())
                {
                    //supporting also relative urls
                    if (!redirectUri.IsAbsoluteUri)
                    {
                        if (redirectUriString.StartsWith("//"))
                            redirectUriString = $"{response.RequestMessage.RequestUri.Scheme}:{redirectUriString}";
                        else
                            redirectUriString = $"{response.RequestMessage.RequestUri.GetLeftPart(UriPartial.Authority)}{redirectUriString}";
                    }
                }

                if (!previewRequest.Redirects.ContainsKey(redirectUriString))
                {
                    previewRequest.CurrentRequestedUrl = new Uri(redirectUriString);

                    return await GetLinkDataAsync(previewRequest);
                }
                else
                {
                    return await GetLinkDataAsync(previewRequest, true);
                }
            }

            Console.WriteLine($"got redirect ({response.StatusCode}) from {previewRequest.CurrentRequestedUrl} with no location header)");

            return null;
        }

        private async Task<LinkInfo> TryGetLinkPreview(HttpResponseMessage response, bool includeDescription)
        {
            var responseContentStream = await response.Content.ReadAsStreamAsync();

            var streamReader = new StreamReader(responseContentStream, Encoding.UTF8);
            var html = await streamReader.ReadToEndAsync();

            html = Regex.Replace(html, @"\t|\n|\r", "");

            return !string.IsNullOrWhiteSpace(html) ? html.ToLinkInfo(response.RequestMessage.RequestUri, includeDescription) : null;
        }


    }
}
