using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Azure;
using Azure.AI.OpenAI;
using Azure.Communication;
using Azure.Communication.CallAutomation;
using Azure.Messaging;
using Azure.Messaging.EventGrid;
using Microsoft.AspNetCore.Mvc;
using System.ComponentModel.DataAnnotations;

var builder = WebApplication.CreateBuilder(args);

// Validate configuration values
var acsConnectionString = builder.Configuration.GetValue<string>("AcsConnectionString");
ArgumentNullException.ThrowIfNullOrEmpty(acsConnectionString, "AcsConnectionString is missing or empty.");

var cognitiveServicesEndpoint = builder.Configuration.GetValue<string>("CognitiveServiceEndpoint");
ArgumentNullException.ThrowIfNullOrEmpty(cognitiveServicesEndpoint, "CognitiveServicesEndpoint is missing or empty.");

var azureOpenAIServiceKey = builder.Configuration.GetValue<string>("AzureOpenAIServiceKey");
ArgumentNullException.ThrowIfNullOrEmpty(azureOpenAIServiceKey, "AzureOpenAIServiceKey is missing or empty.");

var azureOpenAIServiceEndpoint = builder.Configuration.GetValue<string>("AzureOpenAIServiceEndpoint");
ArgumentNullException.ThrowIfNullOrEmpty(azureOpenAIServiceEndpoint, "AzureOpenAIServiceEndpoint is missing or empty.");

var azureOpenAIDeploymentModelName = builder.Configuration.GetValue<string>("AzureOpenAIDeploymentModelName");
ArgumentNullException.ThrowIfNullOrEmpty(azureOpenAIDeploymentModelName, "AzureOpenAIDeploymentModelName is missing or empty.");

var devTunnelUri = builder.Configuration.GetValue<string>("DevTunnelUri");
ArgumentNullException.ThrowIfNullOrEmpty(devTunnelUri, "DevTunnelUri is missing or empty.");

var updateHRMSLogicAppUrl = builder.Configuration.GetValue<string>("UpdateHRMSLogicAppUrl");
ArgumentNullException.ThrowIfNullOrEmpty(updateHRMSLogicAppUrl, "UpdateHRMSLogicAppUrl is missing or empty.");

var sourcePhoneNumber = "+18667464655";

string answerPromptSystemTemplate = """
You are an assistant designed to schedule interviews with candidates.
Your goal is to confirm a suitable date and time for the interview that falls between Monday to Friday, from 9:00 AM to 5:00 PM, excluding public holidays.
Do not validate the date's day of the week or holiday status yourself; the system will handle this.
Analyze the candidate's tone to determine their sentiment score and identify their intent (e.g., confirm date, reschedule, decline, ask questions).
Use a scale of 1-10 (10 being highest) to rate the sentiment score.
Use the following format, replacing the text in brackets with the actual results. Do not include the brackets in the output:

Content: [Briefly confirm or respond to the candidate's message in two sentences. Mention the date/time if provided, and ask if any assistance is needed.]
Score: [Sentiment score based on the candidate's tone]
Intent: [Intent of the candidate's message, e.g., confirm date, request reschedule, unavailable, etc.]
Category: [Classify under scheduling, rescheduling, availability, or general inquiry]
""";

string datePrompt = "Please provide a date for your interview, between Monday and Friday, excluding public holidays. For example, you can say '2nd July' or 'July 9th 2025'. To end the call, say a sentence with the word 'bye'.";
string timePrompt = "Thank you for providing the date. Now, please provide a time between 9:00 AM and 5:00 PM. For example, you can say '9 AM' or '2:30 PM'. To end the call, say a sentence with 'bye'.";
string timeoutSilencePrompt = "I'm sorry, I didn't catch that. Please provide a date or time, or say a sentence with 'bye' to end the call.";
string goodbyePrompt = "Thank you for your time! If you have any questions, feel free to reach out. Have a great day!";
string thankYouPrompt = "Thank you for scheduling your interview. You'll receive a confirmation soon. Have a great day!";
string invalidDatePrompt = "The date provided was invalid. Please provide a future date between Monday and Friday, excluding public holidays, like '2nd July' or 'July 25th 2025', or say a sentence with 'bye' to end the call.";
string invalidTimePrompt = "The time provided was invalid. Please provide a new time between 9:00 AM and 5:00 PM, like '9 AM' or '2:30 PM', or say a sentence with 'bye' to end the call.";

string goodbyeContext = "Goodbye";
string thankYouContext = "ThankYou";
string getDateContext = "GetDate";
string getTimeContext = "GetTime";

string chatResponseExtractPattern = @"\s*Content:(.*)\s*Score:(.*\d+)\s*Intent:(.*)\s*Category:(.*)";

// In-memory storage for confirmed dates, times, and candidate contact numbers
var confirmedDates = new Dictionary<string, DateTime>();
var candidateContactNumbers = new Dictionary<string, string>();
var pendingDates = new Dictionary<string, string>(); // Store date string until time is provided

var client = new CallAutomationClient(acsConnectionString);
var aiClient = new OpenAIClient(new Uri(azureOpenAIServiceEndpoint), new AzureKeyCredential(azureOpenAIServiceKey));

// Register clients for dependency injection
builder.Services.AddSingleton(client);
builder.Services.AddSingleton(aiClient);
var callStatus = new Dictionary<string, string>();
var app = builder.Build();

app.MapGet("/", () => "Hello ACS CallAutomation!");

// Commented out the call logic to disable actual calls

app.MapPost("/startOutboundCall", async ([FromBody] CandidateRequest request, ILogger<Program> logger) =>
{
    logger.LogInformation($"Starting outbound call setup for candidate: '{request.CandidateName}', contact: '{request.CandidateContactNo}'");

    callStatus[request.CandidateContactNo] = "in-progress";

    var caller = new PhoneNumberIdentifier(sourcePhoneNumber);
    var target = new PhoneNumberIdentifier(request.CandidateContactNo);
    var callbackUri = new Uri(new Uri(devTunnelUri), $"/api/callbacks/{Guid.NewGuid()}?callerId={request.CandidateContactNo}");
    logger.LogInformation($"Callback URI: '{callbackUri}'");

    var callInvite = new CallInvite(target, caller);
    var callOptions = new CreateCallOptions(callInvite, callbackUri)
    {
        CallIntelligenceOptions = new CallIntelligenceOptions
        {
            CognitiveServicesEndpoint = new Uri(cognitiveServicesEndpoint)
        }
    };

    logger.LogInformation("Creating outbound call...");
    try
    {
        var result = await client.CreateCallAsync(callOptions);
        var callConnection = result.Value.CallConnection;
        var callMedia = callConnection.GetCallMedia();
        logger.LogInformation($"Outbound call created successfully for connection ID: '{callConnection.CallConnectionId}'");

        bool callConnectedReceived = false;

        // Personalized greeting
        string greeting = $"Hello {request.CandidateName}! This is your HR assistant calling from Novigo Solutions to schedule your technical interview. {datePrompt}";

        // Set up event processors
        client.GetEventProcessor().AttachOngoingEventProcessor<CallConnected>(callConnection.CallConnectionId, async (callConnectedEvent) =>
        {
            callConnectedReceived = true;
            logger.LogInformation($"Call connected event received for connection ID: '{callConnectedEvent.CallConnectionId}'. Starting audio playback...");
            await HandlePlayAsync(greeting, "InitialGreeting", callMedia, logger);
        });

        client.GetEventProcessor().AttachOngoingEventProcessor<CallDisconnected>(callConnection.CallConnectionId, async (callDisconnectedEvent) =>
{
    logger.LogInformation($"Call disconnected event received for ID: '{callDisconnectedEvent.CallConnectionId}'");

    // Cleanup
    confirmedDates.Remove(callDisconnectedEvent.CallConnectionId);
    candidateContactNumbers.TryGetValue(callDisconnectedEvent.CallConnectionId, out var contactNo);
    candidateContactNumbers.Remove(callDisconnectedEvent.CallConnectionId);
    pendingDates.Remove(callDisconnectedEvent.CallConnectionId);

    if (!string.IsNullOrEmpty(contactNo))
    {
        callStatus[contactNo] = callConnectedReceived ? "completed" : "missed";
        logger.LogInformation($"Call status for {contactNo} marked as {(callConnectedReceived ? "completed" : "missed")}.");
    }

    await Task.CompletedTask;
});


        client.GetEventProcessor().AttachOngoingEventProcessor<PlayCompleted>(callConnection.CallConnectionId, async (playCompletedEvent) =>
        {
            logger.LogInformation($"Play completed event received for connection ID: '{playCompletedEvent.CallConnectionId}', context: '{playCompletedEvent.OperationContext}'");
            if (!string.IsNullOrWhiteSpace(playCompletedEvent.OperationContext))
            {
                if (playCompletedEvent.OperationContext.Equals(thankYouContext, StringComparison.OrdinalIgnoreCase) ||
                    playCompletedEvent.OperationContext.Equals(goodbyeContext, StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogInformation("Disconnecting the call...");
                    await callConnection.HangUpAsync(true);
                }
                else if (playCompletedEvent.OperationContext.Equals("InitialGreeting", StringComparison.OrdinalIgnoreCase) ||
                         playCompletedEvent.OperationContext.Equals("FallbackGreeting", StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogInformation("Greeting completed, waiting briefly before starting date recognition...");

                    // Delay added here to give time for the greeting to fully finish and the user to hear the prompt
                    await Task.Delay(1500); // 1.5 seconds pause

                    await HandleRecognizeAsync(
                        callMedia,
                        request.CandidateContactNo,
                        timeoutSilencePrompt,
                        getDateContext,
                        logger,
                        initialSilenceTimeoutSeconds: 30);
                }
            }
        });

        client.GetEventProcessor().AttachOngoingEventProcessor<PlayFailed>(callConnection.CallConnectionId, async (playFailedEvent) =>
        {
            logger.LogError($"Play failed - Message: '{playFailedEvent.ResultInformation?.Message}', Code: '{playFailedEvent.ResultInformation?.Code}', Context: '{playFailedEvent.OperationContext}'");
            await HandleRecognizeAsync(callMedia, request.CandidateContactNo, "I'm having trouble with audio. Can you hear me?", getDateContext, logger);
        });

        client.GetEventProcessor().AttachOngoingEventProcessor<RecognizeCompleted>(callConnection.CallConnectionId, async (recognizeCompletedEvent) =>
        {
            logger.LogInformation($"Recognize completed event received for connection ID: '{recognizeCompletedEvent.CallConnectionId}', OperationContext: '{recognizeCompletedEvent.OperationContext}'");
            var speechResult = recognizeCompletedEvent.RecognizeResult as SpeechResult;
            logger.LogInformation($"SpeechResult: {(speechResult == null ? "null" : $"Speech='{speechResult.Speech}'")}");

            if (!string.IsNullOrWhiteSpace(speechResult?.Speech))
            {
                logger.LogInformation($"Processing recognized speech: '{speechResult.Speech}'");
                if (Regex.IsMatch(speechResult.Speech, @"\bbye\b", RegexOptions.IgnoreCase))
                {
                    logger.LogInformation("Sentence with 'bye' detected. Ending call...");
                    await HandlePlayAsync(goodbyePrompt, goodbyeContext, callMedia, logger);
                }
                else if (recognizeCompletedEvent.OperationContext.Equals(getDateContext, StringComparison.OrdinalIgnoreCase))
                {
                    var (isValidDate, normalizedDate) = await ValidateDateAsync(speechResult.Speech, logger);

                    if (isValidDate)
                    {
                        pendingDates[recognizeCompletedEvent.CallConnectionId] = normalizedDate;
                        logger.LogInformation($"Date stored temporarily: '{normalizedDate}' for call '{recognizeCompletedEvent.CallConnectionId}'");
                        await HandleRecognizeAsync(callMedia, request.CandidateContactNo, timePrompt, getTimeContext, logger);
                    }
                    else
                    {
                        logger.LogInformation("Invalid date detected. Prompting for retry...");
                        await HandleRecognizeAsync(callMedia, request.CandidateContactNo, invalidDatePrompt, getDateContext, logger);
                    }
                }
                else if (recognizeCompletedEvent.OperationContext.Equals(getTimeContext, StringComparison.OrdinalIgnoreCase))
                {
                    var (isValidTime, normalizedTime) = await ValidateTimeAsync(speechResult.Speech, logger);
                    if (isValidTime && pendingDates.ContainsKey(recognizeCompletedEvent.CallConnectionId))
                    {
                        string dateString = pendingDates[recognizeCompletedEvent.CallConnectionId];
                        // Normalize date string for consistent parsing
                        string normalizedDate = NormalizeDateForParsing(dateString, logger);
                        string combinedDateTime = $"{normalizedDate} {normalizedTime}";
                        logger.LogInformation($"Attempting to parse combined date-time: '{combinedDateTime}'");

                        string[] dateTimeFormats = { "d M yyyy h:mmtt", "d M yyyy htt", "M d yyyy h:mmtt", "M d yyyy htt", "d/M/yyyy h:mmtt", "d/M/yyyy htt", "M/d/yyyy h:mmtt", "M/d/yyyy htt" };
                        if (DateTime.TryParseExact(combinedDateTime, dateTimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var confirmedDateTime))
                        {
                            confirmedDates[recognizeCompletedEvent.CallConnectionId] = confirmedDateTime;
                            candidateContactNumbers[recognizeCompletedEvent.CallConnectionId] = request.CandidateContactNo;
                            pendingDates.Remove(recognizeCompletedEvent.CallConnectionId);
                            logger.LogInformation($"DateTime stored: '{confirmedDateTime}' for call '{recognizeCompletedEvent.CallConnectionId}'");

                            // Prepare payload for UpdateHRMSsystem
                            var payload = new
                            {
                                CandidateContactNo = candidateContactNumbers[recognizeCompletedEvent.CallConnectionId],
                                InterviewDate = confirmedDateTime.ToString("dd/MM/yyyy"),
                                InterviewTime = confirmedDateTime.ToString("hh:mmtt")
                            };
                            using var jsonDocument = JsonDocument.Parse(JsonSerializer.Serialize(payload));
                            var jsonElement = jsonDocument.RootElement;

                            // Call UpdateHRMSsystem
                            var updateStatus = await UpdateHRMSsystem(jsonElement, updateHRMSLogicAppUrl, logger);
                            logger.LogInformation($"HRMS update status: '{updateStatus}'");

                            if (updateStatus.Equals("ok", StringComparison.OrdinalIgnoreCase))
                            {
                                await HandlePlayAsync(thankYouPrompt, thankYouContext, callMedia, logger);
                            }
                            else
                            {
                                await HandlePlayAsync("There was an issue scheduling your interview. Please contact HR.", goodbyeContext, callMedia, logger);
                            }
                        }
                        else
                        {
                            logger.LogInformation($"Failed to parse combined date-time: '{combinedDateTime}'. Retrying time...");
                            await HandleRecognizeAsync(callMedia, request.CandidateContactNo, invalidTimePrompt, getTimeContext, logger);
                        }
                    }
                    else
                    {
                        logger.LogInformation("Invalid time detected or no pending date. Prompting for retry...");
                        await HandleRecognizeAsync(callMedia, request.CandidateContactNo, invalidTimePrompt, getTimeContext, logger);
                    }
                }
                else
                {
                    var chatGPTResponse = await GetChatResponseAsync(speechResult.Speech, logger);
                    logger.LogInformation($"Chat GPT response: '{chatGPTResponse}'");
                    Regex regex = new Regex(chatResponseExtractPattern, RegexOptions.Singleline);
                    Match match = regex.Match(chatGPTResponse);
                    if (match.Success)
                    {
                        string answer = match.Groups[1].Value.Trim();
                        logger.LogInformation($"Chat GPT parsed - Answer: '{answer}'");
                        await HandleChatResponseAsync(answer, callMedia, request.CandidateContactNo, logger);
                    }
                    else
                    {
                        logger.LogInformation("No regex match for Chat GPT response. Prompting for date...");
                        await HandleRecognizeAsync(callMedia, request.CandidateContactNo, datePrompt, getDateContext, logger);
                    }
                }
            }
            else
            {
                logger.LogInformation($"No speech recognized. SpeechResult is {(speechResult == null ? "null" : "empty")}. Retrying...");
                string retryPrompt = recognizeCompletedEvent.OperationContext.Equals(getTimeContext, StringComparison.OrdinalIgnoreCase) ? invalidTimePrompt : invalidDatePrompt;
                string retryContext = recognizeCompletedEvent.OperationContext.Equals(getTimeContext, StringComparison.OrdinalIgnoreCase) ? getTimeContext : getDateContext;
                await HandleRecognizeAsync(callMedia, request.CandidateContactNo, retryPrompt, retryContext, logger);
            }
        });

        client.GetEventProcessor().AttachOngoingEventProcessor<RecognizeFailed>(callConnection.CallConnectionId, async (recognizeFailedEvent) =>
        {
            logger.LogError($"Recognize failed - Message: '{recognizeFailedEvent.ResultInformation?.Message}', Code: '{recognizeFailedEvent.ResultInformation?.Code}', SubCode: '{recognizeFailedEvent.ResultInformation?.SubCode}', OperationContext: '{recognizeFailedEvent.OperationContext}', CallConnectionId: '{recognizeFailedEvent.CallConnectionId}'");
            string retryPrompt = recognizeFailedEvent.OperationContext.Equals(getTimeContext, StringComparison.OrdinalIgnoreCase) ? invalidTimePrompt : invalidDatePrompt;
            string retryContext = recognizeFailedEvent.OperationContext.Equals(getTimeContext, StringComparison.OrdinalIgnoreCase) ? getTimeContext : getDateContext;
            logger.LogInformation($"Retrying recognition with prompt: '{retryPrompt}'");
            await HandleRecognizeAsync(callMedia, request.CandidateContactNo, retryPrompt, retryContext, logger);
        });


        _ = Task.Run(async () =>
{
    await Task.Delay(15000);
    if (!callConnectedReceived)
    {
        logger.LogWarning("Call not answered within 15 seconds. Hanging up and marking as missed.");
        callStatus[request.CandidateContactNo] = "missed";
        try
        {
            await callConnection.HangUpAsync(true);
        }
        catch (Exception ex)
        {
            logger.LogError($"Error hanging up unconnected call: {ex.Message}");
        }
    }
});


        return Results.Ok($"Outbound call initiated to '{request.CandidateContactNo}'. Call ID: '{callConnection.CallConnectionId}'");
    }
    catch (Exception ex)
    {
        logger.LogError($"Failed to create outbound call: '{ex.Message}', StackTrace: '{ex.StackTrace}'");
        return Results.Problem($"Failed to initiate call: '{ex.Message}'");
    }
});



app.MapPost("/api/callbacks/{contextId}", async (
    [FromBody] CloudEvent[] cloudEvents,
    [FromRoute] string contextId,
    [Required] string callerId,
    CallAutomationClient callAutomationClient,
    ILogger<Program> logger) =>
{
    logger.LogInformation($"Received '{cloudEvents.Length}' events for contextId: '{contextId}', callerId='{callerId}'");
    foreach (var evt in cloudEvents)
    {
        logger.LogInformation($"Raw Event - Type: '{evt.Type}', Subject: '{evt.Subject}', Data: '{JsonSerializer.Serialize(evt.Data)}'");
    }
    try
    {
        var eventProcessor = client.GetEventProcessor();
        eventProcessor.ProcessEvents(cloudEvents);
        logger.LogInformation("Events processed successfully.");
        return Results.Ok();
    }
    catch (Exception ex)
    {
        logger.LogError($"Error processing callback events: '{ex.Message}', StackTrace: '{ex.StackTrace}'");
        return Results.Problem($"Failed to process callback events: '{ex.Message}'");
    }
});


// New endpoint to test date parsing
async Task HandleChatResponseAsync(string chatResponse, CallMedia callMedia, string callerId, ILogger logger, string context = "OpenAI Sample")
{
    try
    {
        logger.LogInformation($"Handling chat response: '{chatResponse}', callerId: '{callerId}'");
        var chatGPTResponseSource = new TextSource(chatResponse)
        {
            VoiceName = "en-US-NancyNeural"
        };

        var recognizeOptions = new CallMediaRecognizeSpeechOptions(new PhoneNumberIdentifier(callerId))
        {
            InterruptPrompt = true,
            InitialSilenceTimeout = TimeSpan.FromSeconds(30),
            Prompt = chatGPTResponseSource,
            OperationContext = context,
            EndSilenceTimeout = TimeSpan.FromMilliseconds(1000)
        };

        logger.LogInformation($"Starting recognition for context: '{context}' with InitialSilenceTimeout: '{recognizeOptions.InitialSilenceTimeout}' seconds, EndSilenceTimeout: '{recognizeOptions.EndSilenceTimeout}' milliseconds, InterruptPrompt: '{recognizeOptions.InterruptPrompt}'");
        await callMedia.StartRecognizingAsync(recognizeOptions);
        logger.LogInformation($"Recognition started successfully for context: '{context}'");
    }
    catch (Exception ex)
    {
        logger.LogError($"Error in HandleChatResponse: '{ex.Message}', StackTrace: '{ex.StackTrace}'");
        await HandlePlayAsync("I'm having trouble processing your response. Please try again.", "Fallback", callMedia, logger);
    }
}

async Task<(bool IsValid, string NormalizedDate)> ValidateDateAsync(string speechText, ILogger logger)
{
    try
    {
        speechText = Regex.Replace(speechText, @"[.,]", "").Trim().ToLower();
        logger.LogInformation($"Speech text normalized for date: '{speechText}'");

        // Extract date components using regex
        var dateRegex = new Regex(
    @"(?:(\d{1,2}(st|nd|rd|th)?\s*of\s*(january|february|march|april|may|june|july|august|september|october|november|december)\s+\d{4})|" +  // 24th of July 2025
    @"((january|february|march|april|may|june|july|august|september|october|november|december)\s+\d{1,2}(st|nd|rd|th)?\s+\d{4})|" +         // July 24th 2025
    @"(\d{1,2}(st|nd|rd|th)?\s*(january|february|march|april|may|june|july|august|september|october|november|december)\s+\d{4})|" +         // 24th July 2025
    @"(\d{1,2}[-/]\d{1,2}[-/]\d{2,4}))",
    RegexOptions.IgnoreCase
);

        var match = dateRegex.Match(speechText);
        if (!match.Success)
        {
            logger.LogInformation($"No valid date format found in speech: '{speechText}'");
            return (false, string.Empty);
        }

        string datePart = match.Value;
        logger.LogInformation($"Parsed date part: '{datePart}'");

        // Normalize the date string
        string normalizedDate = NormalizeDateForParsing(datePart, logger);
        logger.LogInformation($"Normalized date: '{normalizedDate}'");

        // Try parsing with specific format
        string[] formats = { "d M yyyy" }; // Prioritize day-month-year
        if (DateTime.TryParseExact(normalizedDate, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate))
        {
            // Ensure year is 2025 or later
            if (parsedDate.Year < 2025)
            {
                parsedDate = new DateTime(2025, parsedDate.Month, parsedDate.Day);
                logger.LogInformation($"Adjusted year to 2025: '{parsedDate}'");
            }

            logger.LogInformation($"Parsed date: '{parsedDate}', DayOfWeek: '{parsedDate.DayOfWeek}'");
            if (parsedDate.Date <= DateTime.Today)
            {
                logger.LogInformation($"Invalid: Date '{parsedDate.Date}' is today or in the past.");
                return (false, string.Empty);
            }
            if (parsedDate.DayOfWeek >= DayOfWeek.Monday && parsedDate.DayOfWeek <= DayOfWeek.Friday)
            {
                var publicHolidays = new List<DateTime>
                {
                    new DateTime(2025, 10, 1), new DateTime(2025, 10, 2), new DateTime(2025, 6, 15),
                    new DateTime(2025, 6, 27), new DateTime(2025, 7, 5), new DateTime(2025, 10, 22), new DateTime(2025, 12, 25)
                };
                if (publicHolidays.Contains(parsedDate.Date))
                {
                    logger.LogInformation($"Date '{parsedDate.Date}' is a public holiday.");
                    return (false, string.Empty);

                }
                logger.LogInformation($"Valid date confirmed: '{parsedDate}'");
                return (true, normalizedDate);

            }
            else
            {
                logger.LogInformation($"Invalid: Not Mon-Fri.");
                return (false, string.Empty);

            }
        }
        logger.LogInformation($"Failed to parse date with TryParseExact: '{normalizedDate}'");
        return (false, string.Empty);
    }
    catch (Exception ex)
    {
        logger.LogError($"Error validating date: '{ex.Message}', SpeechText: '{speechText}'");
        return (false, string.Empty);
    }
}

async Task<(bool IsValid, string NormalizedTime)> ValidateTimeAsync(string speechText, ILogger logger)
{
    try
    {
        speechText = speechText.Replace(".", "").Trim().ToLower();
        logger.LogInformation($"Normalized speech text for time: '{speechText}'");

        // Regex for time formats: HH:MM AM/PM or HH AM/PM
        var timeRegex = new Regex(
            @"(\d{1,2}(?::\d{2})?\s*(?:am|pm))",
            RegexOptions.IgnoreCase);
        var match = timeRegex.Match(speechText);
        if (!match.Success)
        {
            logger.LogInformation($"No valid time format found in speech: '{speechText}'");
            return (false, string.Empty);
        }

        string timePart = match.Groups[1].Value;
        logger.LogInformation($"Parsed time part: '{timePart}'");

        logger.LogInformation($"Attempting to parse time: '{timePart}'");
        if (DateTime.TryParse(timePart, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedTime))
        {
            logger.LogInformation($"Parsed time: '{parsedTime.Hour}:{parsedTime.Minute}'");
            if (parsedTime.Hour >= 9 && parsedTime.Hour <= 17)
            {
                // Normalize time format
                string normalizedTime = parsedTime.ToString("h:mmtt", CultureInfo.InvariantCulture).ToUpper();
                logger.LogInformation($"Valid time confirmed: '{normalizedTime}'");
                return (true, normalizedTime);
            }
            else
            {
                logger.LogInformation($"Invalid: Not between 9 AM-5 PM.");
                return (false, string.Empty);
            }
        }
        logger.LogInformation($"Failed to parse time: '{timePart}'");
        return (false, string.Empty);
    }
    catch (Exception ex)
    {
        logger.LogError($"Error validating time: '{ex.Message}', SpeechText: '{speechText}'");
        return (false, string.Empty);
    }
}

string NormalizeDateForParsing(string dateString, ILogger logger)
{
    try
    {
        logger.LogInformation($"Normalizing date string: '{dateString}'");

        // Remove suffixes and "of", and normalize spacing
        dateString = Regex.Replace(dateString, @"(st|nd|rd|th|of)", "", RegexOptions.IgnoreCase);
        dateString = Regex.Replace(dateString, @"[.,]", "");
        dateString = Regex.Replace(dateString, @"\s+", " ").Trim().ToLower();

        string[] tokens = dateString.Split(' ');
        if (tokens.Length < 2)
        {
            logger.LogInformation($"Not enough tokens to parse date: '{dateString}'");
            return dateString;
        }

        var monthMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            {"january", 1}, {"february", 2}, {"march", 3}, {"april", 4}, {"may", 5},
            {"june", 6}, {"july", 7}, {"august", 8}, {"september", 9}, {"october", 10},
            {"november", 11}, {"december", 12}
        };

        int day = 0, month = 0, year = 2025;

        if (monthMap.ContainsKey(tokens[0])) // e.g., july 24 2025
        {
            month = monthMap[tokens[0]];
            int.TryParse(tokens[1], out day);
            if (tokens.Length > 2)
                int.TryParse(tokens[2], out year);
        }
        else if (monthMap.ContainsKey(tokens[1])) // e.g., 24 july 2025
        {
            int.TryParse(tokens[0], out day);
            month = monthMap[tokens[1]];
            if (tokens.Length > 2)
                int.TryParse(tokens[2], out year);
        }
        else
        {
            logger.LogInformation($"Unable to match month in: '{dateString}'");
            return dateString;
        }

        string normalized = $"{day} {month} {year}";
        logger.LogInformation($"Normalized date: '{normalized}'");
        return normalized;
    }
    catch (Exception ex)
    {
        logger.LogError($"Error normalizing date: '{ex.Message}', Input: '{dateString}'");
        return dateString;
    }
}

int GetSentimentScore(string sentimentScore, ILogger logger)
{
    try
    {
        string pattern = @"(\d+)";
        var regex = new Regex(pattern);
        var match = regex.Match(sentimentScore);
        if (match.Success && int.TryParse(match.Value, out int score))
        {
            logger.LogInformation($"Parsed sentiment score: '{score}'");
            return score;
        }
        logger.LogWarning($"Failed to parse sentiment score from: '{sentimentScore}'");
        return -1;
    }
    catch (Exception ex)
    {
        logger.LogError($"Error parsing sentiment score: '{ex.Message}', Input: '{sentimentScore}'");
        return -1;
    }
}

async Task<string> GetChatAsync(string systemPrompt, string userPrompt, ILogger logger)
{
    try
    {
        var chatOptions = new ChatCompletionsOptions
        {
            Messages =
            {
                new ChatMessage(ChatRole.System, systemPrompt),
                new ChatMessage(ChatRole.User, userPrompt)
            },
            MaxTokens = 1000
        };

        logger.LogInformation($"Sending OpenAI request: '{userPrompt}'");
        var response = await aiClient.GetChatCompletionsAsync(azureOpenAIDeploymentModelName, chatOptions);
        var content = response.Value.Choices.Count > 0 ? response.Value.Choices[0].Message.Content : string.Empty;
        logger.LogInformation($"OpenAI response: '{content}'");
        return content ?? "I'm sorry, I can't respond to that right now.";
    }
    catch (Exception ex)
    {
        logger.LogError($"OpenAI error: '{ex.Message}', StackTrace: '{ex.StackTrace}'");
        return "I'm sorry, I'm having trouble processing your request. Please try again.";
    }
}

async Task<string> GetChatResponseAsync(string speechInput, ILogger logger)
{
    var response = await GetChatAsync(answerPromptSystemTemplate, speechInput, logger);
    logger.LogInformation($"Chat response: '{response}'");
    return response;
}

async Task HandleRecognizeAsync(CallMedia callMedia, string callerId, string message, string context, ILogger logger, int initialSilenceTimeoutSeconds = 30)
{
    try
    {
        logger.LogInformation($"Configuring TextSource: voice='en-US-NancyNeural', message='{message}', callerId='{callerId}', context='{context}'");
        var textSource = new TextSource(message)
        {
            VoiceName = "en-US-NancyNeural"
        };

        var recognizeOptions = new CallMediaRecognizeSpeechOptions(new PhoneNumberIdentifier(callerId))
        {
            InterruptPrompt = true,
            InitialSilenceTimeout = TimeSpan.FromSeconds(initialSilenceTimeoutSeconds),
            Prompt = textSource,
            OperationContext = context,
            EndSilenceTimeout = TimeSpan.FromMilliseconds(3000)
        };

        logger.LogInformation($"Starting recognition: message='{message}', InitialSilenceTimeout: '{recognizeOptions.InitialSilenceTimeout.TotalSeconds}' seconds, EndSilenceTimeout: '{recognizeOptions.EndSilenceTimeout.TotalMilliseconds}' ms, InterruptPrompt: '{recognizeOptions.InterruptPrompt}', Context: '{context}'");
        await callMedia.StartRecognizingAsync(recognizeOptions);
        logger.LogInformation($"Recognition started successfully for context: '{context}'");
    }
    catch (Exception ex)
    {
        logger.LogError($"HandleRecognize error: '{ex.Message}', StackTrace: '{ex.StackTrace}', Context: '{context}'");
        await HandlePlayAsync("I'm sorry, I'm having trouble hearing you. Please try again.", "Fallback", callMedia, logger);
    }
}

async Task HandlePlayAsync(string textToPlay, string context, CallMedia callMedia, ILogger logger)
{
    try
    {
        logger.LogInformation($"Playing message: '{textToPlay}', context: '{context}'");
        var playSource = new TextSource(textToPlay)
        {
            VoiceName = "en-US-NancyNeural"
        };

        var playOptions = new PlayToAllOptions(playSource) { OperationContext = context };
        await callMedia.PlayToAllAsync(playOptions);
        logger.LogInformation($"Play command sent successfully for context: '{context}'");
    }
    catch (Exception ex)
    {
        logger.LogError($"HandlePlayAsync error: '{ex.Message}', StackTrace: '{ex.StackTrace}'");
    }
}

static async Task<string> UpdateHRMSsystem(JsonElement payload, string logicAppUrl, ILogger logger)
{
    string jsonPayload = string.Empty;
    try
    {
        jsonPayload = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        logger.LogInformation($"Sending payload to Logic App: '{jsonPayload}'");

        using HttpClient client = new HttpClient();
        var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
        var response = await client.PostAsync(logicAppUrl, content);

        if (response.IsSuccessStatusCode)
        {
            logger.LogInformation("Request sent successfully to Logic App.");
            string responseBody = await response.Content.ReadAsStringAsync();
            logger.LogInformation($"Logic App response: '{responseBody}'");
            return "ok";
        }
        else
        {
            logger.LogError($"Failed to send request to Logic App. Status Code: '{response.StatusCode}'");
            string errorBody = await response.Content.ReadAsStringAsync();
            logger.LogError($"Logic App error: '{errorBody}'");
            return "failed";
        }
    }
    catch (Exception ex)
    {
        logger.LogError($"Error in UpdateHRMSsystem: '{ex.Message}', Payload: '{jsonPayload}', StackTrace: '{ex.StackTrace}'");
        return "failed";
    }
}

app.Run();

public record CandidateRequest([Required] string CandidateName, [Required] string CandidateContactNo);
