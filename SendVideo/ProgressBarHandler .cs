using System;
using System.Threading;

namespace SendVideo
{
    public static class ProgressBarHandler
    {
        private static int _lastProgressLength = 0;
        private static bool _isFirstProgress = true;
        private static readonly object _progressLock = new object();
        private static bool _uploadCompleted = false;

        public static void HandleUploadProgress(long uploadedBytes, long totalBytes, string fileName = "")
        {
            lock (_progressLock)
            {
                if (totalBytes == 0) return;

                double percentage = (double)uploadedBytes / totalBytes * 100;
                int progressBarLength = 30;
                int completedBlocks = (int)Math.Floor(percentage / 100 * progressBarLength);

                string prefix = string.IsNullOrEmpty(fileName) ? "" : $"{fileName} ";
                string progressText = $"\r{prefix}";

                if (!_isFirstProgress)
                {
                    Console.Write(new string(' ', _lastProgressLength) + "\r");
                }
                else
                {
                    _isFirstProgress = false;
                }

                Console.Write(progressText);
                Console.Write("[");

                for (int i = 0; i < progressBarLength; i++)
                {
                    if (i < completedBlocks)
                    {
                        Console.ForegroundColor = GetColor(i, progressBarLength);
                        Console.Write('▇');
                    }
                    else
                    {
                        Console.ResetColor();
                        Console.Write('░');
                    }
                }

                Console.ResetColor();
                Console.Write($"] {percentage:F2}%");

                _lastProgressLength = progressText.Length + progressBarLength + 7;

                if (uploadedBytes >= totalBytes && !_uploadCompleted)
                {
                    _uploadCompleted = true;
                    Thread.Sleep(200);

                    // 从右往左变绿色
                    int currentLeft = Console.CursorLeft - percentage.ToString("F2").Length - 3;
                    // 先输出右侧的 ]
                    Console.CursorLeft = currentLeft;
                    
                    for (int i = progressBarLength - 1; i >= 0; i--)
                    {
                        Console.CursorLeft = currentLeft - progressBarLength + i;
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.Write('▇');
                        Thread.Sleep(20);
                    }
                    Console.ResetColor();
                    Console.WriteLine();
                    _isFirstProgress = true;
                    _uploadCompleted = false;
                }
            }
        }

        private static ConsoleColor GetColor(int position, int totalLength)
        {
            double percent = (double)position / totalLength;
            if (percent < 0.25) return ConsoleColor.Red;
            if (percent < 0.5) return ConsoleColor.Yellow;
            if (percent < 0.75) return ConsoleColor.Blue;
            return ConsoleColor.Cyan;
        }
    }
}