﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Web;
using System.Web.Hosting;
using System.Web.Http;
using Tinifier.Core.Filters;
using Tinifier.Core.Infrastructure;
using Tinifier.Core.Infrastructure.Enums;
using Tinifier.Core.Infrastructure.Exceptions;
using Tinifier.Core.Models;
using Tinifier.Core.Models.Db;
using Tinifier.Core.Services;
using Tinifier.Core.Services.BackendDevs;
using Tinifier.Core.Services.History;
using Tinifier.Core.Services.Media;
using Tinifier.Core.Services.Media.Organizers;
using Tinifier.Core.Services.Settings;
using Tinifier.Core.Services.State;
using Tinifier.Core.Services.TinyPNG;
using Tinifier.Core.Services.Validation;
using Umbraco.Core.Events;
using Umbraco.Core.IO;
using Umbraco.Web.WebApi;
using System.Linq;
using System;
using Umbraco.Core.Models;
using Umbraco.Web;
using System.IO;
using Newtonsoft.Json.Linq;

namespace Tinifier.Core.Controllers
{
    [ExceptionFilter]
    public class TinifierController : UmbracoAuthorizedApiController
    {
        private readonly IImageService _imageService;
        private readonly ITinyPNGConnector _tinyPngConnectorService;
        private readonly IHistoryService _historyService;
        private readonly ISettingsService _settingsService;
        private readonly IStateService _stateService;
        private readonly IValidationService _validationService;
        private readonly IBackendDevsConnector _backendDevsConnectorService;
        private readonly IMediaHistoryService _mediaHistoryService;

        public TinifierController()
        {
            _imageService = new ImageService();
            _historyService = new HistoryService();
            _tinyPngConnectorService = new TinyPNGConnectorService();
            _settingsService = new SettingsService();
            _stateService = new StateService();
            _validationService = new ValidationService();
            _backendDevsConnectorService = new BackendDevsConnectorService();
            _mediaHistoryService = new ImageService();
        }

        /// <summary>
        /// Get Image by id
        /// </summary>
        /// <param name="timageId">Image Id</param>
        /// <returns>Response(StatusCode, {image, history}}</returns>
        [HttpGet]
        public HttpResponseMessage GetTImage(string timageId)
        {
            TImage timage;

            try
            {
                if (int.TryParse(timageId, out var imageId))
                    timage = _imageService.GetImage(imageId);
                else
                    timage = _imageService.GetCropImage(Base64Decode(timageId));
            }
            catch (Exception ex)
            {
                return Request.CreateResponse(HttpStatusCode.BadRequest,
                    new TNotification("Tinifier Oops", ex.Message, EventMessageType.Error)
                    {
                        sticky = true,
                    });
            }

            var history = _historyService.GetImageHistory(timage.Id);
            return Request.CreateResponse(HttpStatusCode.OK, new { timage, history });
        }

        public static string Base64Decode(string base64EncodedData)
        {
            var base64EncodedBytes = System.Convert.FromBase64String(base64EncodedData);
            return System.Text.Encoding.UTF8.GetString(base64EncodedBytes);
        }

        /// <summary>
        /// Tinify Image(s)
        /// </summary>
        /// <param name="imageRelativeUrls">Array of media items urls</param>
        /// <param name="mediaId">Media item id</param>
        /// <returns>Response(StatusCode, message)</returns>
        [HttpGet]
        public async Task<HttpResponseMessage> TinyTImage([FromUri]string[] imageRelativeUrls, [FromUri]int mediaId)
        {
            HttpResponseMessage responseMessage;
            _settingsService.CheckIfSettingExists();
            _validationService.ValidateConcurrentOptimizing();

            if (imageRelativeUrls.Length != 0)
            {
                responseMessage = await TinifyImages(imageRelativeUrls);
            }
            else
            {
                if (_validationService.IsFolder(mediaId))
                {
                    responseMessage = await TinifyFolder(mediaId);
                }
                else
                {
                    responseMessage = await TinifyImage(mediaId);
                }
            }

            return responseMessage;
        }

        /// <summary>
        /// Tinify full Media folder
        /// </summary>
        /// <returns>Response(StatusCode, message)</returns>
        [HttpPut]
        public async Task<HttpResponseMessage> TinifyEverything()
        {
            var nonOptimizedImages = new List<TImage>();

            foreach (var image in _imageService.GetAllImages())
            {
                var imageHistory = _historyService.GetImageHistory(image.Id);
                if (imageHistory != null && imageHistory.IsOptimized)
                    continue;
                nonOptimizedImages.Add(image);
            }

            // Get all published content and tinify all crops 
            var allPublishedContent = new List<IPublishedContent>();

            foreach (var publishedContentRoot in Umbraco.TypedContentAtRoot())
                allPublishedContent.AddRange(publishedContentRoot.DescendantsOrSelf());

            foreach(var content in allPublishedContent)
            {
                var imageCroppers = content.Properties.Where(x => !string.IsNullOrEmpty(x.Value.ToString()) && x.Value.ToString().Contains("crops"));

                foreach (var crop in imageCroppers)
                {
                    var imagePath = crop.Value;

                    if(imagePath != null)
                    {
                        var json = JObject.Parse(imagePath.ToString());
                        var path = json.GetValue("src").ToString();

                        if (string.IsNullOrEmpty(path))
                            throw new EntityNotFoundException();

                        var fileExt = Path.GetExtension(path).ToUpper().Replace(".", string.Empty).Trim();
                        if (!PackageConstants.SupportedExtensions.Contains(fileExt))
                            throw new NotSupportedExtensionException(fileExt);

                        var pathForFolder = path.Remove(path.LastIndexOf('/') + 1);
                        var serverPathForFolder = HttpContext.Current.Server.MapPath(pathForFolder);

                        var di = new DirectoryInfo(serverPathForFolder);
                        var files = di.GetFiles();

                        foreach (var file in files)
                        {
                            TImage image = new TImage
                            {
                                Id = Path.Combine(pathForFolder, file.Name),
                                Name = file.Name,
                                AbsoluteUrl = Path.Combine(pathForFolder, file.Name)
                            };

                            var imageHistory = _historyService.GetImageHistory(image.Id);
                            if (imageHistory != null && imageHistory.IsOptimized)
                                continue;

                            nonOptimizedImages.Add(image);
                        }
                    }                    
                }
            }

            if (nonOptimizedImages.Count == 0)
                return GetImageOptimizedReponse(true);

            _stateService.CreateState(nonOptimizedImages.Count);
            return await CallTinyPngService(nonOptimizedImages);
        }

        /// <summary>
        /// Tinify folder By Id
        /// </summary>
        /// <param name="folderId">Folder Id</param>
        /// <returns>Response(StatusCode, message)</returns>
        private async Task<HttpResponseMessage> TinifyFolder(int folderId)
        {
            var images = _imageService.GetFolderImages(folderId);
            var imagesList = _historyService.GetImagesWithoutHistory(images);

            if (imagesList.Count == 0)
                return GetImageOptimizedReponse(true);

            _stateService.CreateState(imagesList.Count);
            return await CallTinyPngService(imagesList);
        }

        /// <summary>
        /// Tinify Images by urls
        /// </summary>
        /// <param name="imagesRelativeUrls">Array of images urls</param>
        /// <returns>Response(StatusCode, message)</returns>
        private async Task<HttpResponseMessage> TinifyImages(IEnumerable<string> imagesRelativeUrls)
        {
            var nonOptimizedImages = new List<TImage>();

            foreach (var imageRelativeUrl in imagesRelativeUrls)
            {
                var image = _imageService.GetImage(imageRelativeUrl);
                var imageHistory = _historyService.GetImageHistory(image.Id);

                if (imageHistory != null && imageHistory.IsOptimized)
                    continue;

                nonOptimizedImages.Add(image);
            }

            if (nonOptimizedImages.Count == 0)
                return GetImageOptimizedReponse(true);

            _stateService.CreateState(nonOptimizedImages.Count);
            return await CallTinyPngService(nonOptimizedImages);
        }

        /// <summary>
        /// Tinify image by Id
        /// </summary>
        /// <param name="imageId">Image Id</param>
        /// <returns>Response(StatusCode, message)</returns>
        private async Task<HttpResponseMessage> TinifyImage(int imageId)
        {
            var imageById = _imageService.GetImage(imageId);

            var notOptimizedImage = _historyService.GetImageHistory(imageById.Id);
            if (notOptimizedImage != null && notOptimizedImage.IsOptimized)
                return GetImageOptimizedReponse();

            var nonOptimizedImages = new List<TImage> { imageById };
            _stateService.CreateState(nonOptimizedImages.Count);
            return await CallTinyPngService(nonOptimizedImages, SourceTypes.Image);
        }

        /// <summary>
        /// Create request to TinyPNG service and get response
        /// </summary>
        /// <param name="imagesList">Images that needs to be tinifing</param>
        /// <param name="sourceType">Folder or Image</param>
        /// <returns>Response(StatusCode, message)</returns>
        private async Task<HttpResponseMessage> CallTinyPngService(IEnumerable<TImage> imagesList, SourceTypes sourceType = SourceTypes.Folder)
        {
            var nonOptimizedImagesCount = 0;
            var userDomain = HttpContext.Current.Request.Url.Host;
            var fs = FileSystemProviderManager.Current.GetFileSystemProvider<MediaFileSystem>();
            foreach (TImage tImage in imagesList)
            {
                var tinyResponse = await _tinyPngConnectorService.TinifyAsync(tImage, fs);

                if (tinyResponse.Output.Url == null)
                {
                    _historyService.CreateResponseHistory(tImage.Id.ToString(), tinyResponse);
                    _stateService.UpdateState();
                    nonOptimizedImagesCount++;
                    continue;
                }

                _imageService.UpdateImageAfterSuccessfullRequest(tinyResponse, tImage, fs);

                try
                {
                    HostingEnvironment.QueueBackgroundWorkItem(stat => _backendDevsConnectorService.SendStatistic(userDomain));
                }
                catch (NotSuccessfullRequestException)
                {
                    continue;
                }
            }

            int n = imagesList.Count();
            int k = n - nonOptimizedImagesCount;

            return GetSuccessResponse(k, n,
                nonOptimizedImagesCount == 0 ? EventMessageType.Success : EventMessageType.Warning);
        }

        private HttpResponseMessage GetImageOptimizedReponse(bool isMultipleImages = false)
        {
            return Request.CreateResponse(HttpStatusCode.OK,
                   new TNotification(
                       PackageConstants.TinifyingFinished,
                       isMultipleImages ? PackageConstants.AllImagesAlreadyOptimized : PackageConstants.AlreadyOptimized,
                       EventMessageType.Info)
                       );
        }

        private HttpResponseMessage GetSuccessResponse(int optimized, int total, EventMessageType type)
        {
            return Request.CreateResponse(HttpStatusCode.OK,
                new TNotification(PackageConstants.TinifyingFinished,
                    $"{optimized}/{total} images were optimized. Enjoy the package? Click the message and rate us!", type)
                {
                    url = "https://our.umbraco.org/projects/backoffice-extensions/tinifier/"
                });
        }

        [HttpGet]
        public HttpResponseMessage OrganizeImages(int folderId)
        {
            if (MediaSavingHelper.IsSavingInProgress)
            {
                return Request.CreateResponse(HttpStatusCode.Conflict);
            }
            try
            {
                var organizer = new ByUploadedDateImageOrganizer(folderId);
                organizer.Organize();

                return Request.CreateResponse(HttpStatusCode.OK);
            }
            catch (OrganizationConstraintsException c)
            {
                return GetErrorNotification(c.Message, HttpStatusCode.OK, EventMessageType.Warning);
            }
            catch (Exception ex)
            {
                return GetErrorNotification(ex.Message, HttpStatusCode.InternalServerError, EventMessageType.Error);
            }
        }

        [HttpGet]
        public HttpResponseMessage DiscardOrganizing(int folderId)
        {
            try
            {
                _mediaHistoryService.DiscardOrganizing(folderId);
                return Request.CreateResponse(HttpStatusCode.OK);
            }
            catch (OrganizationConstraintsException c)
            {
                return GetErrorNotification(c.Message, HttpStatusCode.OK, EventMessageType.Warning);
            }
            catch (Exception ex)
            {
                return GetErrorNotification(ex.Message, HttpStatusCode.InternalServerError, EventMessageType.Error);
            }
        }

        private HttpResponseMessage GetErrorNotification(string message, HttpStatusCode httpStatusCode, EventMessageType eventMessageType)
        {
            return Request.CreateResponse(httpStatusCode,
                    new TNotification("Tinifier Oops", message, eventMessageType) { sticky = true });
        }
    }
}