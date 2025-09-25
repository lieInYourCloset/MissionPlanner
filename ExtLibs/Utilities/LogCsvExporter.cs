using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using log4net;

namespace MissionPlanner.Utilities
{
    /// <summary>
    /// Export flight log data to CSV format
    /// </summary>
    public class LogCsvExporter
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public static void ExportToCSV(string logFile, Action<int> progressCallback = null)
        {
            string csvFile = Path.ChangeExtension(logFile, ".csv");
            
            try
            {
                using (var csvWriter = new StreamWriter(csvFile, false, Encoding.UTF8))
                {
                    // Write CSV header
                    csvWriter.WriteLine("Time,MessageType,Lat,Lng,Alt,Spd,Hdg,VX,VY,VZ,Roll,Pitch,Yaw,RawData");

                    if (logFile.ToLower().EndsWith(".bin"))
                    {
                        ExportBinaryLog(logFile, csvWriter, progressCallback);
                    }
                    else
                    {
                        ExportTextLog(logFile, csvWriter, progressCallback);
                    }
                }
            }
            catch (Exception ex)
            {
                log.Error("Error exporting log to CSV", ex);
                throw;
            }
        }

        private static void ExportBinaryLog(string logFile, StreamWriter csvWriter, Action<int> progressCallback)
        {
            using (var reader = new StreamReader(logFile))
            {
                var dfLogBuffer = new DFLogBuffer(reader.BaseStream);
                var totalLines = dfLogBuffer.Count;
                var processedLines = 0;

                foreach (var line in dfLogBuffer)
                {
                    if (string.IsNullOrEmpty(line))
                        continue;

                    try
                    {
                        var csvData = ProcessLogLine(line, dfLogBuffer.dflog);
                        
                        if (!string.IsNullOrEmpty(csvData))
                        {
                            csvWriter.WriteLine(csvData);
                        }
                    }
                    catch (Exception ex)
                    {
                        log.Debug($"Error processing line: {line}", ex);
                    }

                    processedLines++;
                    if (progressCallback != null && processedLines % 1000 == 0)
                    {
                        int progress = (int)((double)processedLines / totalLines * 100);
                        progressCallback(progress);
                    }
                }

                dfLogBuffer.Dispose();
            }
        }

        private static void ExportTextLog(string logFile, StreamWriter csvWriter, Action<int> progressCallback)
        {
            var dflog = new DFLog(null);
            var lines = File.ReadAllLines(logFile);
            var totalLines = lines.Length;

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                
                if (string.IsNullOrEmpty(line))
                    continue;

                try
                {
                    var csvData = ProcessLogLine(line, dflog);
                    
                    if (!string.IsNullOrEmpty(csvData))
                    {
                        csvWriter.WriteLine(csvData);
                    }
                }
                catch (Exception ex)
                {
                    log.Debug($"Error processing line: {line}", ex);
                }

                if (progressCallback != null && i % 1000 == 0)
                {
                    int progress = (int)((double)i / totalLines * 100);
                    progressCallback(progress);
                }
            }
        }

        private static string ProcessLogLine(string line, DFLog dflog)
        {
            var items = line.Split(',', ':');
            
            if (items.Length < 1)
                return null;

            var messageType = items[0].Trim();

            // Process FMT messages to understand log format
            if (messageType == "FMT")
            {
                try
                {
                    dflog.FMTLine(line);
                }
                catch (Exception ex)
                {
                    log.Debug($"Error processing FMT line: {line}", ex);
                }
                return null; // Don't export FMT lines to CSV
            }

            // Focus on GPS data primarily, but include other important messages
            if (messageType.StartsWith("GPS") && dflog.logformat.ContainsKey("GPS"))
            {
                return ProcessGPSMessage(line, items, dflog);
            }
            else if (messageType == "ATT" && dflog.logformat.ContainsKey("ATT"))
            {
                return ProcessAttitudeMessage(line, items, dflog);
            }
            else if (messageType == "POS" && dflog.logformat.ContainsKey("POS"))
            {
                return ProcessPositionMessage(line, items, dflog);
            }

            return null;
        }

        private static string ProcessGPSMessage(string line, string[] items, DFLog dflog)
        {
            try
            {
                var statusIndex = dflog.FindMessageOffset("GPS", "Status");
                var latIndex = dflog.FindMessageOffset("GPS", "Lat");
                var lngIndex = dflog.FindMessageOffset("GPS", "Lng");
                var altIndex = dflog.FindMessageOffset("GPS", "Alt");
                var spdIndex = dflog.FindMessageOffset("GPS", "Spd");
                var hdgIndex = dflog.FindMessageOffset("GPS", "GCrs");
                var vxIndex = dflog.FindMessageOffset("GPS", "VX");
                var vyIndex = dflog.FindMessageOffset("GPS", "VY");
                var vzIndex = dflog.FindMessageOffset("GPS", "VZ");

                // Check GPS status - only export if we have a valid fix (3D lock or better)
                if (statusIndex != -1 && statusIndex < items.Length)
                {
                    if (!int.TryParse(items[statusIndex], out int status) || status < 3)
                        return null;
                }

                var time = dflog.GetTimeGPS(line);
                var timeStr = time != DateTime.MinValue ? time.ToString("yyyy-MM-dd HH:mm:ss.fff") : "";

                var lat = GetSafeDouble(items, latIndex);
                var lng = GetSafeDouble(items, lngIndex);
                var alt = GetSafeDouble(items, altIndex);
                var spd = GetSafeDouble(items, spdIndex);
                var hdg = GetSafeDouble(items, hdgIndex);
                var vx = GetSafeDouble(items, vxIndex);
                var vy = GetSafeDouble(items, vyIndex);
                var vz = GetSafeDouble(items, vzIndex);

                // Format CSV line: Time,MessageType,Lat,Lng,Alt,Spd,Hdg,VX,VY,VZ,Roll,Pitch,Yaw,RawData
                return $"{timeStr},GPS,{lat},{lng},{alt},{spd},{hdg},{vx},{vy},{vz},,,,\"{line.Replace("\"", "\"\"")}\"";
            }
            catch (Exception ex)
            {
                log.Debug($"Error processing GPS message: {line}", ex);
                return null;
            }
        }

        private static string ProcessAttitudeMessage(string line, string[] items, DFLog dflog)
        {
            try
            {
                var rollIndex = dflog.FindMessageOffset("ATT", "Roll");
                var pitchIndex = dflog.FindMessageOffset("ATT", "Pitch");
                var yawIndex = dflog.FindMessageOffset("ATT", "Yaw");

                var roll = GetSafeDouble(items, rollIndex);
                var pitch = GetSafeDouble(items, pitchIndex);
                var yaw = GetSafeDouble(items, yawIndex);

                // Use a generic time since ATT messages don't have GPS time
                var timeStr = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");

                return $"{timeStr},ATT,,,,,,,,,{roll},{pitch},{yaw},\"{line.Replace("\"", "\"\"")}\"";
            }
            catch (Exception ex)
            {
                log.Debug($"Error processing ATT message: {line}", ex);
                return null;
            }
        }

        private static string ProcessPositionMessage(string line, string[] items, DFLog dflog)
        {
            try
            {
                var latIndex = dflog.FindMessageOffset("POS", "Lat");
                var lngIndex = dflog.FindMessageOffset("POS", "Lng");
                var altIndex = dflog.FindMessageOffset("POS", "Alt");

                var lat = GetSafeDouble(items, latIndex);
                var lng = GetSafeDouble(items, lngIndex);
                var alt = GetSafeDouble(items, altIndex);

                var timeStr = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");

                return $"{timeStr},POS,{lat},{lng},{alt},,,,,,,,\"{line.Replace("\"", "\"\"")}\"";
            }
            catch (Exception ex)
            {
                log.Debug($"Error processing POS message: {line}", ex);
                return null;
            }
        }

        private static double GetSafeDouble(string[] items, int index)
        {
            if (index == -1 || index >= items.Length)
                return 0.0;

            if (double.TryParse(items[index], NumberStyles.Float, CultureInfo.InvariantCulture, out double result))
                return result;

            return 0.0;
        }
    }
}