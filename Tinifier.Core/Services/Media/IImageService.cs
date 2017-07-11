﻿using System.Collections.Generic;
using Tinifier.Core.Models.API;
using Tinifier.Core.Models.Db;

namespace Tinifier.Core.Services.Media
{
    public interface IImageService
    {
        /// <summary>
        /// Get Image By Id from Umbraco Media
        /// </summary>
        /// <param name="id">Image Id</param>
        /// <returns>TImage</returns>
        TImage GetImage(int id);

        /// <summary>
        /// Get image by path
        /// </summary>
        /// <param name="path">Image path</param>
        /// <returns>TImage</returns>
        TImage GetImage(string path);

        /// <summary>
        /// Get all optimized images
        /// </summary>
        /// <returns>IEnumerable of TImage</returns>
        IEnumerable<TImage> GetOptimizedImages();

        /// <summary>
        /// Get all images from specific folder
        /// </summary>
        /// <param name="folderId">Folder Id</param>
        /// <returns>IEnumerable of TImage</returns>
        IEnumerable<TImage> GetFolderImages(int folderId);

        /// <summary>
        /// Optimize image and update history
        /// </summary>
        /// <param name="image">TImage</param>
        void OptimizeImage(TImage image);

        /// <summary>
        ///  Update image and state if its image from folder
        /// </summary>
        /// <param name="tinyResponse">Response from TinyPNG</param>
        /// <param name="image">TImage</param>
        void UpdateImageAfterSuccessfullRequest(TinyResponse tinyResponse, TImage image);
    }
}
