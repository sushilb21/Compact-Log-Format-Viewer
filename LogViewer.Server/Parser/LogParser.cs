﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LogViewer.Server.Extensions;
using LogViewer.Server.Models;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Filters.Expressions;
using Serilog.Formatting.Compact.Reader;


namespace LogViewer.Server
{
    public class LogParser : ILogParser
    {
        private List<LogEvent> _logItems;
        private string _logFilePath;
        public bool LogIsOpen { get; set; }

        private const string ExpressionOperators = "()+=*<>%-";

        public LogParser()
        {
            _logItems = new List<LogEvent>();
            _logFilePath = string.Empty;
            LogIsOpen = false;
        }
        
        public List<LogEvent> ReadLogs(string filePath, Logger logger = null)
        {
            var logItems = new List<LogEvent>();

            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                using (var stream = new StreamReader(fs))
                {
                    try
                    {
                        var reader = new LogEventReader(stream);
                        while (reader.TryRead(out var evt))
                        {
                            if (logger != null)
                            {
                                //We can persist the log item (using the passed in Serilog config)
                                //In this case a Logger with File Sink setup
                                logger.Write(evt);
                            }

                            logItems.Add(evt);
                        }
                    }
                    catch (Exception)
                    {
                        throw;
                    }
                }
            }

            _logItems = logItems;
            _logFilePath = filePath;
            LogIsOpen = true;

            return _logItems;
        }

        public LogLevelCounts TotalCounts()
        {
            var counts = new LogLevelCounts
            {
                Verbose = _logItems.Count(log => log.Level == LogEventLevel.Verbose),
                Information = _logItems.Count(log => log.Level == LogEventLevel.Information),
                Debug = _logItems.Count(log => log.Level == LogEventLevel.Debug),
                Warning = _logItems.Count(log => log.Level == LogEventLevel.Warning),
                Error = _logItems.Count(log => log.Level == LogEventLevel.Error),
                Fatal = _logItems.Count(log => log.Level == LogEventLevel.Fatal)
            };

            return counts;
        }

        public int TotalErrors()
        {
            return _logItems.Count(log => log.Level == LogEventLevel.Fatal || log.Level == LogEventLevel.Error || log.Exception != null);
        }

        public void ExportTextFile(string messageTemplate, string newFileName)
        {
            if (string.IsNullOrEmpty(messageTemplate))
                messageTemplate = "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}";

            var loggerConfig = new LoggerConfiguration()
                .WriteTo.File(newFileName, outputTemplate: messageTemplate);

            //We will need to re-read logs & pass in a Serilog that can persist to TXT file
            using (var logger = loggerConfig.CreateLogger())
            {
                ReadLogs(_logFilePath, logger);
            }
        }

        

        public PagedResult<LogMessage> Search(int pageNumber = 1, int pageSize = 100, string filterExpression = null)
        {
            //If filter null - return a simple page of results
            if(filterExpression == null)
            {
                var totalRecords = _logItems.Count;
                var logMessages = _logItems                   
                    .Skip(pageSize * (pageNumber - 1))
                    .Take(pageSize)
                    .Select(x => new LogMessage
                    {
                        Timestamp = x.Timestamp,
                        Level = x.Level,
                        MessageTemplateText = x.MessageTemplate.Text,
                        Exception = x.Exception?.ToString(),
                        Properties = x.Properties,
                        RenderedMessage = x.RenderMessage()
                    });

                return new PagedResult<LogMessage>(totalRecords, pageNumber, pageSize)
                {
                    Items = logMessages
                };
            }


            Func<LogEvent, bool> filter;

            // If the expression is one word and doesn't contain a serilog operator then we can perform a like search
            if (!filterExpression.Contains(" ") && !filterExpression.ContainsAny(ExpressionOperators.Select(c => c)))
            {
                filter = PerformMessageLikeFilter(filterExpression);
            }
            else // check if it's a valid expression
            {
                // If the expression evaluates then make it into a filter
                if (FilterLanguage.TryCreateFilter(filterExpression, out var eval, out _))
                {
                    filter = evt => true.Equals(eval(evt));
                }
                else
                {
                    //Assume the expression was a search string and make a Like filter from that
                    filter = PerformMessageLikeFilter(filterExpression);
                }
            }

            //Apply the filter to the collection
            var filteredLogs = _logItems.Where(filter);
            var filteredTotal = filteredLogs.Count();
            var logItems = filteredLogs
                    .Skip(pageSize * (pageNumber - 1))
                    .Take(pageSize)
                    .Select(x => new LogMessage
                    {
                        Timestamp = x.Timestamp,
                        Level = x.Level,
                        MessageTemplateText = x.MessageTemplate.Text,
                        Exception = x.Exception?.ToString(),
                        Properties = x.Properties,
                        RenderedMessage = x.RenderMessage()
                    });

            return new PagedResult<LogMessage>(filteredTotal, pageNumber, pageSize)
            {
                Items = logItems
            };
        }

        public List<LogTemplate> GetMessageTemplates()
        {
            var templates = _logItems
                .GroupBy(log => log.MessageTemplate.Text)
                .Select(x => new LogTemplate
                {
                    MessageTemplate = x.Key,
                    Count = x.Count()
                })
                .OrderByDescending(x => x.Count);

            return templates.ToList();
        }

        private Func<LogEvent, bool> PerformMessageLikeFilter(string filterExpression)
        {
            var filterSearch = $"@Message like '%{FilterLanguage.EscapeLikeExpressionContent(filterExpression)}%'";
            if (FilterLanguage.TryCreateFilter(filterSearch, out var eval, out _))
            {
                return evt => true.Equals(eval(evt));
            }

            return null;
        }
    }
}
