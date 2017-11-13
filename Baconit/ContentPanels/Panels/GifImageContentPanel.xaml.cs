using Baconit.Interfaces;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.Storage.Streams;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;
using Windows.Web.Http;
using HtmlAgilityPack;
using Windows.UI.Xaml.Media.Imaging;

namespace Baconit.ContentPanels.Panels
{
    public sealed partial class GifImageContentPanel : UserControl, IContentPanel
    {
        /// <summary>
        /// Holds a reference to our base.
        /// </summary>
        IContentPanelBaseInternal m_base;

        /// <summary>
        /// Indicates if we should be playing or not.
        /// </summary>
        bool m_shouldBePlaying = false;

        /// <summary>
        /// Holds a reference to the video we are playing.
        /// </summary>
        MediaElement m_gifVideo;
        Image m_gifImage;

        public GifImageContentPanel(IContentPanelBaseInternal panelBase)
        {
            this.InitializeComponent();
            m_base = panelBase;
        }

        /// <summary>
        /// Called by the host when it queries if we can handle a post.
        /// </summary>
        /// <param name="post"></param>
        /// <returns></returns>
        public static async Task<bool> CanHandlePostAsync(ContentPanelSource source)
        {
            // See if we can find a imgur, gfycat gif, or a normal gif we can send to gfycat.
            if (String.IsNullOrWhiteSpace(await GetImgurUrlAsync(source)) && String.IsNullOrWhiteSpace(GetGfyCatApiUrl(source.Url)) && String.IsNullOrWhiteSpace(GetGifUrl(source.Url)))
            {
                return false;
            }
            return true;
        }

        #region IContentPanel

        /// <summary>
        /// Indicates how large the panel is in memory.
        /// </summary>
        public PanelMemorySizes PanelMemorySize
        {
            get
            {
                // #todo can we figure this out?
                return PanelMemorySizes.Medium;
            }
        }

        /// <summary>
        /// Fired when we should load the content.
        /// </summary>
        /// <param name="source"></param>
        public void OnPrepareContent()
        {
            // Run the rest on a background thread.
            Task.Run(async () =>
            {
                // Try to get the imgur url
                string gifUrl = await GetImgurUrlAsync(m_base.Source);

                // If that failed try to get a url from GfyCat
                if (gifUrl.Equals(String.Empty))
                {
                    // We have to get it from gfycat
                    gifUrl = await GetGfyCatGifUrl(GetGfyCatApiUrl(m_base.Source.Url));

                    if(String.IsNullOrWhiteSpace(gifUrl))
                    {
                        // If these failed it might just be a gif. try to send it to gfycat for conversion.
                        gifUrl = await ConvertGifUsingGfycat(m_base.Source.Url);
                    }
                }

                // Since some of this can be costly, delay the work load until we aren't animating.
                await Windows.ApplicationModel.Core.CoreApplication.MainView.CoreWindow.Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
                {
                    // If we weren't able to convert the gif using gifycat
                    if (String.IsNullOrWhiteSpace(gifUrl))
                    {
                        lock(this)
                        {
                            // Make sure we aren't destroyed.
                            if (m_base.IsDestoryed)
                            {
                                return;
                            }

                            m_gifImage = new Image();
                            m_gifImage.Tapped += OnGifImageTapped;
                            BitmapImage bitmapImage = new BitmapImage();
                            //m_gifImage.Width = bitmapImage.DecodePixelWidth = 80;
                            // Natural px width of image source.
                            // You don't need to set Height; the system maintains aspect ratio, and calculates the other
                            // dimension, as long as one dimension measurement is provided.
                            bitmapImage.UriSource = new Uri(m_base.Source.Url, UriKind.Absolute);
                            bitmapImage.ImageOpened += OnGifImageLoadComplete;
                            bitmapImage.ImageFailed += OnGifImageLoadComplete;
                            bitmapImage.AutoPlay = true;
                            m_gifImage.Source = bitmapImage;
                            // Add the video to the root                    
                            ui_contentRoot.Children.Add(m_gifImage);
                        }
                        //m_base.FireOnFallbackToBrowser();
                        //App.BaconMan.TelemetryMan.ReportUnexpectedEvent(this, "FailedToShowGifAfterConfirm");
                        return;
                    }

                    lock(this)
                    {
                        // Make sure we aren't destroyed.
                        if (m_base.IsDestoryed)
                        {
                            return;
                        }

                        // Create the media element
                        m_gifVideo = new MediaElement();
                        m_gifVideo.HorizontalAlignment = HorizontalAlignment.Stretch;
                        m_gifVideo.Tapped += OnVideoTapped;
                        m_gifVideo.CurrentStateChanged += OnVideoCurrentStateChanged;
                        m_gifVideo.IsLooping = true;

                        // Set the source
                        m_gifVideo.Source = new Uri(gifUrl, UriKind.Absolute);
                        m_gifVideo.Play();

                        // Add the video to the root                    
                        ui_contentRoot.Children.Add(m_gifVideo);
                    }
                });
            });
        }

        /// <summary>
        /// Fired when we should destroy our content.
        /// </summary>
        public void OnDestroyContent()
        {
            lock(this)
            {
                // Destroy the video
                if (m_gifVideo != null)
                {
                    m_gifVideo.CurrentStateChanged -= OnVideoCurrentStateChanged;
                    m_gifVideo.Tapped -= OnVideoTapped;
                    m_gifVideo.Stop();
                    m_gifVideo = null;
                }

                if (m_gifImage != null)
                {
                    m_gifImage.Tapped -= OnGifImageTapped;
                    BitmapImage bitmapImage = (BitmapImage)m_gifImage.Source;
                    if(bitmapImage.IsPlaying)
                    {
                        bitmapImage.Stop();
                    }

                    bitmapImage.ImageOpened -= OnGifImageLoadComplete;
                    bitmapImage.ImageFailed -= OnGifImageLoadComplete;
                    m_gifImage = null;
                }

                // Clear vars
                m_shouldBePlaying = false;

                // Clear the UI
                ui_contentRoot.Children.Clear();
            } 
        }

        /// <summary>
        /// Fired when a new host has been added.
        /// </summary>
        public void OnHostAdded()
        {
            // Ignore for now.
        }

        /// <summary>
        /// Fired when this post becomes visible
        /// </summary>
        public void OnVisibilityChanged(bool isVisible)
        {
            lock (this)
            {
                // Set that we should be playing
                m_shouldBePlaying = isVisible;

                if (m_gifVideo != null)
                {
                    // Call the action. If we are already playing or paused this
                    // will do nothing.
                    if(isVisible)
                    {
                        m_gifVideo.Play();
                    }
                    else
                    {
                        m_gifVideo.Pause();
                    }
                }

                if (m_gifImage != null)
                {
                    var bitmap = (BitmapImage)m_gifImage.Source;
                    if (isVisible)
                    {
                        bitmap.Play();
                    }
                    else
                    {
                        bitmap.Stop();
                    }
                }
            }
        }

        #endregion

        #region Video Playback

        /// <summary>
        /// Hides the loading and fades in the video when it start playing.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void OnVideoCurrentStateChanged(object sender, RoutedEventArgs e)
        {
            // If we start playing and the loading UI isn't hidden do so.
            if (m_base.IsLoading && m_gifVideo.CurrentState == MediaElementState.Playing)
            {
                m_base.FireOnLoading(false);
            }

            // Make sure if we are playing that we should be (that we are actually visible)
            if (!m_shouldBePlaying && m_gifVideo.CurrentState == MediaElementState.Playing)
            {
                m_gifVideo.Pause();
            }            
        }

        private void OnGifImageLoadComplete(object sender, RoutedEventArgs e)
        {
            if (m_base.IsLoading)
            {
                m_base.FireOnLoading(false);
            }
        }

        /// <summary>
        /// Fired when the gif is tapped
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void OnVideoTapped(object sender, TappedRoutedEventArgs e)
        {
            if (m_gifVideo != null)
            {
                if (m_gifVideo.CurrentState == MediaElementState.Playing)
                {
                    m_gifVideo.Pause();
                }
                else
                {
                    m_gifVideo.Play();
                }
            }
        }

        private void OnGifImageTapped(object sender, TappedRoutedEventArgs e)
        {
            if (m_gifImage != null)
            {
                var bitmap = (BitmapImage)m_gifImage.Source;
                if (bitmap.IsPlaying)
                {
                    bitmap.Stop();
                }
                else
                {
                    bitmap.Play();
                }
            }
        }

        #endregion

        #region Gif Url Parsing

        private static HttpClient s_httpClient = new HttpClient();

        /// <summary>
        /// Tries to get a Imgur gif url
        /// </summary>
        /// <param name="postUrl"></param>
        /// <returns></returns>
        private static async Task<string> GetImgurUrlAsync(ContentPanelSource source)
        {
            var postUrl = source.Url;
            Uri uri;
            if (!Uri.TryCreate(postUrl, UriKind.Absolute, out uri))
            {
                return String.Empty;
            }

            if (!uri.Host.EndsWith("imgur.com", StringComparison.OrdinalIgnoreCase))
            {
                return String.Empty;
            }

            if (!String.IsNullOrEmpty(source.AltUrl))
            {
                return source.AltUrl;
            }

            var filename = uri.Segments.Last();

            if (filename.Equals("/")) // There is no filename
            {
                return String.Empty;
            }

            if (filename.EndsWith(".gifv", StringComparison.OrdinalIgnoreCase))
            {
                filename = filename.Replace(".gifv", ".mp4");
            }
            else if (filename.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
            {
                filename = filename.Replace(".gif", ".mp4");
            }
            else if (!filename.Contains("."))
            {
                return (await GetImgUrMp4UriAsync(uri)).ToString();
            }
            else
            {
                return string.Empty;
            }

            var builder = new UriBuilder();
            builder.Host = "imgur.com";
            builder.Path = "/" + filename;
            return builder.ToString();
        }

        private static async Task<string> GetImgUrMp4UriAsync(Uri uri)
        {
            var response = await GetPageAsync(uri);
            var html = await response.Content.ReadAsStringAsync();
            var doc = new HtmlDocument();
            doc.LoadHtml(html);
            return FindMp4ContentUrl(doc.DocumentNode.SelectNodes(@"//meta[@name='twitter:player:stream']"));
        }

        private static async Task<HttpResponseMessage> GetPageAsync(Uri uri)
        {
            uri = FixImgUrUri(uri);
            while (true)
            {
                var response = await s_httpClient.GetAsync(uri);

                // if the link is on the main imgur.com domain but has a valid file ending, it will be redirected to i.imgur.com
                // so make sure the redirected link is on the main imgur.com domain
                var redirectedUri = response.RequestMessage.RequestUri;
                uri = FixImgUrUri(redirectedUri);
                if (redirectedUri.Equals(uri))
                {
                    return response;
                }

                response.Dispose();
            }
        }

        static string FindMp4ContentUrl(HtmlNodeCollection nodes)
        {
            if(nodes == null)
            {
                return String.Empty;
            }

            Uri uri;
            foreach (var node in nodes)
            {
                if (Uri.TryCreate(node.Attributes["content"]?.Value, UriKind.Absolute, out uri))
                {
                    if (uri.Segments.Last().EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
                    {
                        return uri.ToString();
                    }
                }
            }

            return String.Empty;
        }

        static Uri FixImgUrUri(Uri uri)
        {
            if (uri.Host.Equals("i.imgur.com", StringComparison.OrdinalIgnoreCase))
            {
                var builder = new UriBuilder(uri);
                builder.Host = "imgur.com";
                builder.Query = "";
                return builder.Uri;
            }

            return uri;
        }

        /// <summary>
        /// Attempts to find a .gif in the url.
        /// </summary>
        /// <param name="postUrl"></param>
        /// <returns></returns>
        private static string GetGifUrl(string postUrl)
        {
            // Send the url to lower, but we need both because some websites
            // have case sensitive urls.
            string postUrlLower = postUrl.ToLower();

            int lastSlash = postUrlLower.LastIndexOf('/');
            if(lastSlash != -1)
            {
                string urlEnding = postUrlLower.Substring(lastSlash);
                if(urlEnding.Contains(".gif") || urlEnding.Contains(".gif?"))
                {
                    return postUrl;
                }
            }
            return String.Empty;
        }

        /// <summary>
        /// Tries to get a gfy cat api url.
        /// </summary>
        /// <param name="postUrl"></param>
        /// <returns></returns>
        private static string GetGfyCatApiUrl(string postUrl)
        {
            Uri uri;
            if (!Uri.TryCreate(postUrl, UriKind.Absolute, out uri))
            {
                return String.Empty;
            }
            
            if (!uri.Host.EndsWith("gfycat.com", StringComparison.OrdinalIgnoreCase))
            {
                return String.Empty;
            }

            String gifName = uri.Segments.Last();
            if (gifName.Equals("/")) // Empty path
            {
                return String.Empty;
            }

            return $"http://gfycat.com/cajax/get/" + gifName;
        }

        // Disable this annoying warning.
#pragma warning disable CS0649

        private class GfyCatDataContainer
        {
            [JsonProperty(PropertyName = "gfyItem")]
            public GfyItem item;
        }

        private class GfyItem
        {
            [JsonProperty(PropertyName = "mp4Url")]
            public string Mp4Url;
        }

#pragma warning restore

        /// <summary>
        /// Gets a video url from gfycat
        /// </summary>
        /// <param name="apiUrl"></param>
        /// <returns></returns>
        private async Task<string> GetGfyCatGifUrl(string apiUrl)
        {
            // Return if we have nothing.
            if (apiUrl.Equals(String.Empty))
            {
                return String.Empty;
            }

            try
            {
                // Make the call
                IHttpContent webResult = await App.BaconMan.NetworkMan.MakeGetRequest(apiUrl);

                // Get the input stream and json reader.
                // NOTE!! We are really careful not to use a string here so we don't have to allocate a huge string.
                IInputStream inputStream = await webResult.ReadAsInputStreamAsync();
                using (StreamReader reader = new StreamReader(inputStream.AsStreamForRead()))
                using (JsonReader jsonReader = new JsonTextReader(reader))
                {           
                    // Parse the Json as an object
                    JsonSerializer serializer = new JsonSerializer();
                    GfyCatDataContainer gfyData = await Task.Run(() => serializer.Deserialize<GfyCatDataContainer>(jsonReader));

                    // Validate the response
                    string mp4Url = gfyData.item.Mp4Url;
                    if (String.IsNullOrWhiteSpace(mp4Url))
                    {
                        throw new Exception("Gfycat response failed to parse");
                    }

                    // Return the url
                    return mp4Url;
                }     
            }
            catch (Exception e)
            {
                App.BaconMan.MessageMan.DebugDia("failed to get image from gfycat", e);
                App.BaconMan.TelemetryMan.ReportUnexpectedEvent(this, "FaileGfyCatApiCall", e);
            }

            return String.Empty;
        }

        // Disable this annoying warning.
#pragma warning disable CS0649

        private class GfyCatConversionData
        {
            [JsonProperty(PropertyName = "mp4Url")]
            public string Mp4Url;
        }

#pragma warning restore

        /// <summary>
        /// Uses GfyCat to convert a normal .gif into a video
        /// </summary>
        /// <param name="apiUrl"></param>
        /// <returns></returns>
        private async Task<string> ConvertGifUsingGfycat(string gifUrl)
        {
            // Return if we have nothing.
            if (gifUrl.Equals(String.Empty))
            {
                return String.Empty;
            }

            try
            {
                // Make the call
                IHttpContent webResult = await App.BaconMan.NetworkMan.MakeGetRequest("https://upload.gfycat.com/transcode?fetchUrl="+gifUrl);

                // Get the input stream and json reader.
                // NOTE!! We are really careful not to use a string here so we don't have to allocate a huge string.
                IInputStream inputStream = await webResult.ReadAsInputStreamAsync();
                using (StreamReader reader = new StreamReader(inputStream.AsStreamForRead()))
                using (JsonReader jsonReader = new JsonTextReader(reader))
                {
                    // Parse the Json as an object
                    JsonSerializer serializer = new JsonSerializer();
                    GfyCatConversionData gfyData = await Task.Run(() => serializer.Deserialize<GfyCatConversionData>(jsonReader));

                    // Validate the response
                    string mp4Url = gfyData.Mp4Url;
                    if (String.IsNullOrWhiteSpace(mp4Url))
                    {
                        throw new Exception("Gfycat failed to convert");
                    }

                    // Return the url
                    return mp4Url;
                }
            }
            catch (Exception e)
            {
                App.BaconMan.MessageMan.DebugDia("failed to convert gif via gfycat", e);
                App.BaconMan.TelemetryMan.ReportUnexpectedEvent(this, "GfyCatConvertFailed", e);
            }

            return String.Empty;
        }

        #endregion
    }
}
