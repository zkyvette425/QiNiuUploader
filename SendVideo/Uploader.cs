using Qiniu.Storage;
using Qiniu.Http;
using Qiniu.Util;
using System.IO;
using System.Collections.Concurrent; // 添加此命名空间

namespace SendVideo;

public class Uploader(string accessKey, string secretKey, string bucket)
{
    private readonly Mac _mac = new(accessKey, secretKey);
    private readonly HttpManager _httpManager = new();
    private const int MaxRetry = 3;
    
    private readonly Config _config = new()
    {
        Zone = Zone.ZONE_CN_South,
        UseHttps = true,
        UseCdnDomains = true,
        ChunkSize = ChunkUnit.U512K
    };

    // 新增：按 .progress 文件路径分组的锁（不同文件用不同锁，避免全局阻塞）
    private readonly ConcurrentDictionary<string, object> _progressLocks = new();
    
    public HttpResult UploadFile(string filePath, string key)
    {
        var fileInfo = new System.IO.FileInfo(filePath);
        if (!fileInfo.Exists)
        {
            Console.WriteLine($"文件 {filePath} 不存在，无法上传。");
            return new HttpResult { Code = 404, RefText = "文件不存在" };
        }

        if (fileInfo.Length == 0)
        {
            Console.WriteLine($"检测到空文件 {key}，使用简单上传（非分片）。");
            return UploadEmptyFile(key);
        }
        
        return UploadNormalFile(filePath, key, fileInfo.Length);
    }

    private HttpResult UploadNormalFile(string filePath, string key, long fileSize)
    {
        var putPolicy = new PutPolicy { Scope = $"{bucket}:{key}", DeleteAfterDays = 0 };
        putPolicy.SetExpires(3600);
        var token = Auth.CreateUploadToken(_mac, putPolicy.ToJsonString());
        var extra = new PutExtra
        {
            ResumeRecordFile = Path.Combine(Path.GetDirectoryName(filePath), $"{key}.progress"),
            Version = "v2",
            PartSize = 4 * 1024 * 1024, // 4MB 分片
            BlockUploadThreads = 1 // 关键：设置为单线程上传，避免 SDK 内部多线程冲突（牺牲部分性能换稳定性）
        };

        string progressPath = extra.ResumeRecordFile;
        object fileLock = _progressLocks.GetOrAdd(progressPath, new object());

        for (int retry = 0; retry < MaxRetry; retry++)
        {
            try
            {
                lock (fileLock) // 核心：整个上传过程加锁，包括 SDK 对进度文件的所有操作
                {
                    using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
                    var uploader = new ResumableUploader(_config);
                    
                    return uploader.UploadStream(stream, key, token, extra);
                }
            }
            catch (IOException ex) when (ex.Message.Contains("被占用"))
            {
                Console.WriteLine($"进度文件被占用，重试 {retry + 1}/{MaxRetry}");
                if (retry == MaxRetry - 1)
                {
                    _progressLocks.TryRemove(progressPath, out _);
                    return new HttpResult { Code = 500, RefText = "上传重试失败" };
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"上传失败：{ex.Message}");
                _progressLocks.TryRemove(progressPath, out _);
                return new HttpResult { Code = 500, RefText = "上传失败" };
            }
        }
        return new HttpResult { Code = 500, RefText = "上传重试失败" };
    }

    private HttpResult UploadEmptyFile(string key)
    {
        var putPolicy = new PutPolicy
        {
            Scope = bucket,
            DeleteAfterDays = 0
        };
        putPolicy.SetExpires(3600);

        var token = Auth.CreateUploadToken(_mac, putPolicy.ToJsonString());

        FormUploader formUploader = new FormUploader(_config);
        string tempEmptyFilePath = Path.GetTempFileName();
        try
        {
            return formUploader.UploadFile(tempEmptyFilePath, key, token, null);
        }
        finally
        {
            if (File.Exists(tempEmptyFilePath))
            {
                File.Delete(tempEmptyFilePath);
            }
        }
    }
}