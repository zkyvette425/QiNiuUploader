using Qiniu.Storage;
using Qiniu.Http;
using Qiniu.Util;
using System.Collections.Concurrent; // 添加此命名空间

namespace SendVideo;

public class Uploader(string accessKey, string secretKey, string bucket)
{
    private readonly Mac _mac = new(accessKey, secretKey);
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

    public HttpResult UploadFile(string filePath, string originKey, string key)
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

        return UploadNormalFile(filePath, originKey, key, fileInfo.Length);
    }

    private HttpResult UploadNormalFile(string filePath, string originKey, string key, long fileSize)
    {
        var putPolicy = new PutPolicy { Scope = $"{bucket}:{key}", DeleteAfterDays = 0 };
        putPolicy.SetExpires(3600);
        var token = Auth.CreateUploadToken(_mac, putPolicy.ToJsonString());

        // 根据文件大小动态调整分片大小
        int partSize = fileSize > 100 * 1024 * 1024 ? 8 * 1024 * 1024 : 4 * 1024 * 1024;

        var extra = new PutExtra
        {
            ResumeRecordFile = Path.Combine(Path.GetDirectoryName(filePath), $"{originKey}.progress"),
            Version = "v2",
            PartSize = partSize,
            BlockUploadThreads = 1,
            ProgressHandler = (uploaded, total) => ProgressBarHandler.HandleUploadProgress(uploaded, total, originKey)
        };

        string progressPath = extra.ResumeRecordFile;
        object fileLock = _progressLocks.GetOrAdd(progressPath, new object());

        try
        {
            for (int retry = 0; retry < MaxRetry; retry++)
            {
                try
                {
                    lock (fileLock)
                    {
                        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
                        var uploader = new ResumableUploader(_config);

                        var result = uploader.UploadStream(stream, key, token, extra);

                        if (result.Code == 200)
                        {
                            Console.WriteLine($"文件 {originKey} 上传成功");
                            return result;
                        }

                        if (!ShouldRetry(result.Code))
                        {
                            Console.WriteLine($"文件 {originKey} 上传失败，错误码: {result.Code}，不进行重试");
                            return result;
                        }

                        Console.WriteLine($"文件 {originKey} 上传失败，错误码: {result.Code}，进行第 {retry + 1} 次重试");

                        // 指数退避
                        if (retry < MaxRetry - 1)
                            Thread.Sleep((int)Math.Pow(2, retry) * 1000);
                    }
                }
                catch (IOException ex) when (ex.Message.Contains("被占用"))
                {
                    Console.WriteLine($"进度文件被占用，重试 {retry + 1}/{MaxRetry}");
                    Thread.Sleep(1000 * (retry + 1)); // 递增等待时间

                    if (retry == MaxRetry - 1)
                    {
                        return new HttpResult { Code = 500, RefText = "进度文件被占用，重试失败" };
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"上传失败：{ex.Message}");
                    return new HttpResult { Code = 500, RefText = $"上传失败: {ex.Message}" };
                }
            }

            return new HttpResult { Code = 500, RefText = "上传重试次数已耗尽" };
        }
        finally
        {
            // 确保锁对象被移除
            _progressLocks.TryRemove(progressPath, out _);

            // 检查并清理可能残留的进度文件
            try
            {
                if (File.Exists(extra.ResumeRecordFile))
                {
                    File.Delete(extra.ResumeRecordFile);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"清理进度文件失败: {ex.Message}");
            }
        }
    }

// 判断是否应该重试的辅助方法
    private bool ShouldRetry(int code)
    {
        // 服务器错误或临时错误，可以重试
        return code >= 500 || code == 429 || code == 408;
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