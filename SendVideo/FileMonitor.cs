using Qiniu.Http;
namespace SendVideo;

public class FileMonitor
{
    private readonly string _gameRecordingFolder;
    private readonly string _videoFolder;
    private readonly int _checkIntervalSeconds;
    private readonly Uploader _uploader;
    private readonly UploadRecord _record;
    private readonly object _fileOperationLock = new object();

    public FileMonitor()
    {
        _gameRecordingFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "游戏录制"
        );
        _videoFolder = Path.Combine(_gameRecordingFolder, "视频");
        _checkIntervalSeconds = AppSettings.CheckIntervalSeconds;
        _record = new UploadRecord(Path.Combine(_gameRecordingFolder, "record.db"));
        _uploader = new Uploader(AppSettings.AccessKey, AppSettings.SecretKey, AppSettings.Bucket);
        ResetStuckUploadingStatusOnStartup();
    }

    public void StartMonitoring()
    {
        while (true)
        {
            try
            {
                var folder = FoldersExist();
                if (!string.IsNullOrEmpty(folder))
                {
                    Console.WriteLine($"等待其他程序创建文件夹:{folder}，将于 {_checkIntervalSeconds} 秒后重试");
                    Thread.Sleep(_checkIntervalSeconds * 1000);
                    continue;
                }

                UpdateDatabaseWithNewFiles();
                UploadFilesBasedOnDatabaseStatus();
                DeleteOldUploadedFiles();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"监控过程中出现错误: {ex.Message}");
            }

            Thread.Sleep(_checkIntervalSeconds * 1000);
        }
    }

    private string? FoldersExist()
    {
        if (!Directory.Exists(_gameRecordingFolder))
        {
            return _gameRecordingFolder;
        }
        if (!Directory.Exists(_videoFolder))
        {
            return _videoFolder;
        }
        return null;
    }

    private void UpdateDatabaseWithNewFiles()
    {
        lock (_fileOperationLock)
        {
            var existingFiles = _record.GetAllFiles().Select(f => f.FileName).ToList();
            var candidateFiles = Directory.GetFiles(_videoFolder)
                .Select(Path.GetFileName)
                .Where(f => !existingFiles.Contains(f) && !f.EndsWith(".progress", StringComparison.OrdinalIgnoreCase));

            foreach (var candidateFile in candidateFiles)
            {
                string filePath = Path.Combine(_videoFolder, candidateFile);
                // 检查文件是否可正常读取（避免处理未粘贴完成的文件）
                if (IsFileReadyForProcessing(filePath))
                {
                    _record.AddNewFile(candidateFile);
                    Console.WriteLine($"已将新文件 {candidateFile} 添加到数据库，状态为未上传");
                }
            }
        }
    }

    /// <summary>
    /// 检查文件是否可读写（非锁定状态，粘贴已完成）
    /// </summary>
    private bool IsFileReadyForProcessing(string filePath)
    {
        try
        {
            // 尝试以独占模式打开文件，若成功则文件未被占用（粘贴完成）
            using FileStream fs = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            // 文件被占用，暂不处理
            return false;
        }
    }

    private void UploadFilesBasedOnDatabaseStatus()
    {
        lock (_fileOperationLock)
        {
            CleanInvalidUploadingStatus();
            
            // 检查是否有文件正在上传
            if (_record.HasFileInUploadingStatus())
            {
                Console.WriteLine("有文件正在上传，等待上传完成...");
                return;
            }

            // 优先处理中断上传的文件
            var interruptedFiles = _record.GetInterruptedFiles();
            foreach (var file in interruptedFiles)
            {
                string filePath = Path.Combine(_videoFolder, file.FileName);
                HandleFile(filePath, file.FileName, "中断上传");
            }

            // 处理未上传的文件
            var notUploadedFiles = _record.GetNotUploadedFiles();
            foreach (var file in notUploadedFiles)
            {
                string filePath = Path.Combine(_videoFolder, file.FileName);
                HandleFile(filePath, file.FileName, "未上传");
            }
        }
    }
    
    /// <summary>
    /// 清理无效的“上传中”状态（文件不存在或进度文件丢失）
    /// </summary>
    private void CleanInvalidUploadingStatus()
    {
        var uploadingFiles = _record.GetAllFiles().Where(f => f.Status == "上传中").ToList();
        foreach (var file in uploadingFiles)
        {
            string filePath = Path.Combine(_videoFolder, file.FileName);
            string progressFilePath = Path.Combine(_videoFolder, $"{file.FileName}.progress");

            // 若文件不存在或进度文件不存在，视为中断
            if (!File.Exists(filePath) || !File.Exists(progressFilePath))
            {
                _record.MarkFileAsInterrupted(file.FileName);
                Console.WriteLine($"清理无效的上传中状态：{file.FileName}");
            }
        }
    }
    
    private void ResetStuckUploadingStatusOnStartup()
    {
        lock (_fileOperationLock)
        {
            var uploadingFiles = _record.GetAllFiles().Where(f => f.Status == "上传中").ToList();
            foreach (var file in uploadingFiles)
            {
                _record.MarkFileAsInterrupted(file.FileName);
                Console.WriteLine($"启动时重置上传中状态：{file.FileName} 为中断上传");
            }
        }
    }

    private void HandleFile(string filePath, string fileName, string expectedStatus)
    {
        if (!File.Exists(filePath))
        {
            _record.MarkFileAsLocalDeleted(fileName);
            return;
        }

        var fileStatus = _record.GetAllFiles().FirstOrDefault(f => f.FileName == fileName)?.Status;
        if (fileStatus == expectedStatus)
        {
            TryUploadFile(filePath, fileName);
        }
    }

    private void TryUploadFile(string filePath, string key)
    {
        int maxRetries = 3;
        for (int retry = 0; retry < maxRetries; retry++)
        {
            try
            {
                Console.WriteLine($"开始上传文件: {key}");
                
                _record.MarkFileAsUploading(key); // 标记为上传中
                HttpResult result = _uploader.UploadFile(filePath, key);

                if (result.Code == 200)
                {
                    _record.MarkFileAsUploaded(key);
                    DeleteProgressFile(key);
                    Console.WriteLine($"文件 {key} 上传成功，已删除对应的 .progress 文件");
                    return; // 成功时提前退出
                }
                else
                {
                    Console.WriteLine($"上传失败（状态码 {result.Code}），重试 {retry + 1}/{maxRetries}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"上传异常（重试 {retry + 1}/{maxRetries}）: {ex.Message}");
            }

            // 单次失败后，清除“上传中”状态（避免数据库残留）
            _record.MarkFileAsInterrupted(key); 

            // 非最后一次重试时，添加退避时间（可选，提升稳定性）
            if (retry < maxRetries - 1)
            {
                Thread.Sleep(TimeSpan.FromSeconds(1 << retry)); // 1s → 2s → 4s 退避
            }
        }

        // 所有重试失败后，最终标记为中断
        Console.WriteLine($"文件 {key} 上传失败，达到最大重试次数（{maxRetries} 次）");
    }

    private void DeleteProgressFile(string key)
    {
        string progressFilePath = Path.Combine(_videoFolder, $"{key}.progress");
        if (File.Exists(progressFilePath))
        {
            try
            {
                File.Delete(progressFilePath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"删除 {progressFilePath} 时出错: {ex.Message}");
            }
        }
    }

    private void DeleteOldUploadedFiles()
    {
        lock (_fileOperationLock)
        {
            var oldFiles = _record.GetUploadedFilesOlderThanDays(7);
            foreach (var file in oldFiles)
            {
                string filePath = Path.Combine(_videoFolder, file.FileName);
                if (File.Exists(filePath))
                {
                    try
                    {
                        File.Delete(filePath);
                        _record.MarkFileAsLocalDeleted(file.FileName);
                        Console.WriteLine($"已删除本地旧文件: {file.FileName}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"删除文件 {file.FileName} 时出错: {ex.Message}");
                    }
                }
                else
                {
                    _record.MarkFileAsLocalDeleted(file.FileName);
                }
            }
        }
    }
}
    