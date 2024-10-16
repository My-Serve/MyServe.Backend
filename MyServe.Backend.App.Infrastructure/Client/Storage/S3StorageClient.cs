using System.Net;
using System.Net.Mime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Util;
using MimeMapping;
using MyServe.Backend.App.Application.Client;
using MyServe.Backend.App.Application.Extensions;
using MyServe.Backend.Common.Exceptions.Storage;
using MyServe.Backend.Common.Models;
using MyServe.Backend.Common.Options;
using Serilog;

namespace MyServe.Backend.App.Infrastructure.Client.Storage;

public abstract class S3StorageClient(IAmazonS3 s3Client, ILogger logger, BucketCustomConfiguration bucketCustomConfiguration) : IStorageClient
{
    public async Task<Uri?> UploadAsync(FileContent fileContent, bool publicRead = false, params string[] filePath)
    {
        if(filePath.Length < 2)
            throw new ArgumentException("File path should contain at least 2 elements. Bucket and file name", nameof(filePath));

        var bucketName = bucketCustomConfiguration.Bucket;
        if (!await EnsureBucketExistsAsync(bucketCustomConfiguration.Bucket))
            throw new FailedBucketGenerationException(bucketName);
        var key = string.Join("/", filePath);
        
        key = GetAndSetFileNameWithExtensions(fileContent, key);
        
        var putObjectRequest = ConstructPutObjectRequest(fileContent, publicRead, bucketName, key);

        var uploadResponse = await s3Client.PutObjectAsync(putObjectRequest);
        if (uploadResponse is null)
            return null;
        
        // If there is no custom domain
        if (string.IsNullOrWhiteSpace(bucketCustomConfiguration.CustomDomainUrl))
        {
            var endpoint = s3Client.DetermineServiceOperationEndpoint(putObjectRequest);
            return new Uri($"{endpoint.URL}{key}");
        }
        
        return new Uri($"{bucketCustomConfiguration.CustomDomainUrl}/{bucketName}/{key}");
    }

    protected virtual PutObjectRequest ConstructPutObjectRequest(FileContent fileContent, bool publicRead,
        string bucketName, string key)
    {
        var putObjectRequest = new PutObjectRequest
        {
            BucketName = bucketName,
            Key = key,
            InputStream = fileContent.FileStream,
            ContentType = (fileContent.ContentType ?? new ContentType()).MediaType,
            Metadata =
            {
                ["Content-Type"] = (fileContent.ContentType ?? new ContentType()).MediaType
            },
            CannedACL = publicRead ? S3CannedACL.PublicRead : S3CannedACL.Private
        };

        return putObjectRequest;
    }

    private string GetAndSetFileNameWithExtensions(FileContent fileContent, string key)
    {
        if (fileContent.ContentType is not null)
        {
            var extensions = MimeUtility.GetExtensions(fileContent.ContentType.MediaType);
            if (extensions is not null && extensions.Length >= 1)
            {
                key = $"{key}.{extensions.First()}";
                logger.Information("Identified the file with file type "+extensions.First());
            }
            else
            {
                logger.Warning($"No valid file extension was found for mime type {fileContent.ContentType.MediaType}");
            }
        }
        else
        {
            logger.Information("No content type has been specified to determine extension");
        }

        return key;
    }

    public Task DeleteAsync(Uri uri)
    {
        throw new NotImplementedException();
    }

    public async Task<IEnumerable<Uri>> DeleteMultipleAsync(IEnumerable<Uri> uris)
    {
        var deleteObjectsRequest = new DeleteObjectsRequest()
        {
            BucketName = bucketCustomConfiguration.Bucket,
            Quiet = true
        };
        List<Uri> failed = [];
        foreach (var uri in uris)
        {
            var objectInfo = bucketCustomConfiguration.FromUrl(uri);
            if (objectInfo is null)
            {
                logger.Warning("Failed to determine the storage key for object {Url} on bucket {Bucket}", uri, deleteObjectsRequest.BucketName);
                failed.Add(uri);
                continue;
            }
            
            deleteObjectsRequest.AddKey(objectInfo.Value.Key);
            logger.Information("Identified object: {ObjectKey} on {Bucket}", objectInfo.Value.Key, deleteObjectsRequest.BucketName);
        }

        var deletionResponse = await s3Client.DeleteObjectsAsync(deleteObjectsRequest);
        logger.Information("The deletion request has been completed and has charged {Value}",deletionResponse.RequestCharged?.Value);
        
        deletionResponse.DeleteErrors.ForEach(x =>
        {
            logger.Error("Failed to delete {Key} due to {Code} - {Reason}", x.Key, x.Code, x.Message);
            failed.Add(new Uri($"{bucketCustomConfiguration.CustomDomainUrl}/{x.Key}"));
        });
        return failed;
    }

    public async Task<Uri> GeneratePreSignedUrlAsync(SignedStorageAccessOptions accessOptions, params string[] filePath)
    {
        var getPreSignedUrlRequest = new GetPreSignedUrlRequest()
        {
            BucketName = bucketCustomConfiguration.Bucket,
            Expires = accessOptions.Expiry,
            Verb = Enum.Parse<HttpVerb>(accessOptions.Action),
            Key = string.Join("/", filePath)
        };

        var preSignedUrl = await s3Client.GetPreSignedURLAsync(getPreSignedUrlRequest);
        return new Uri(preSignedUrl);
    }

    protected virtual async Task<bool> EnsureBucketExistsAsync(string bucketName)
    {
        if (!AmazonS3Util.ValidateV2Bucket(bucketName))
        {
            throw new ArgumentException("Bucket name is not valid.");
        }
        
        if (!await AmazonS3Util.DoesS3BucketExistV2Async(s3Client,bucketName))
        {
            var bucketRequest = new PutBucketRequest()
            {
                BucketName = bucketName,
                UseClientRegion = true,
                CannedACL = S3CannedACL.PublicRead
            };

            var response = await s3Client.PutBucketAsync(bucketRequest);
            return response?.HttpStatusCode == HttpStatusCode.OK;
        }

        return true;
    }
}