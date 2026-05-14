using Amazon.Lambda.Core;
using Amazon.Lambda.Serialization.SystemTextJson;
using Amazon.S3;
using Amazon.SimpleEmailV2;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;
using PdfSharp.Fonts;
using PdfWatermarkLambda.Fonts;
using PdfWatermarkLambda.Models;
using PdfWatermarkLambda.Services;

[assembly: LambdaSerializer(typeof(DefaultLambdaJsonSerializer))]

namespace PdfWatermarkLambda;

public class PdfWatermarkFunction
{
    private readonly PdfStampingService _service;

    public PdfWatermarkFunction()
    {
        // Register font resolver FIRST — Lambda runs on Linux with no system fonts
        GlobalFontSettings.FontResolver = new LambdaFontResolver();

        try { BsonSerializer.RegisterSerializer(new GuidSerializer(BsonType.String)); }
        catch (BsonSerializationException) { /* already registered in warm container */ }

        var mongoConnStr = GetRequiredEnv("MONGODB_CONNECTION_STRING");
        var mongoDbName  = Env("MONGODB_DATABASE_NAME", "VitalityHub");
        var bucketName   = GetRequiredEnv("S3_BUCKET_NAME");
        var fromEmail    = GetRequiredEnv("FROM_EMAIL");

        var db = new MongoClient(mongoConnStr).GetDatabase(mongoDbName);
        // AmazonS3Client() and AmazonSimpleEmailServiceV2Client() with no args
        // use the Lambda IAM execution role automatically.
        var s3  = new AmazonS3Client();
        var ses = new AmazonSimpleEmailServiceV2Client();

        _service = new PdfStampingService(db, s3, bucketName, ses, fromEmail);
    }

    public async Task FunctionHandler(StampInvoicePayload payload, ILambdaContext context)
    {
        context.Logger.LogInformation(
            $"PdfWatermarkLambda triggered — Action={payload.Action ?? "WatermarkAndZip"} Uuid={payload.Uuid} CompanyId={payload.CompanyId}");

        if (string.IsNullOrWhiteSpace(payload.Uuid))
        {
            context.Logger.LogError("Invalid payload: Uuid is missing.");
            return;
        }

        if (payload.Action == "GenerateInvoiceZip")
        {
            await _service.GenerateInvoiceZipAsync(payload, context.Logger);
        }
        else if (payload.Action == "GeneratePaymentZip")
        {
            if (string.IsNullOrWhiteSpace(payload.PdfS3Key))
            {
                context.Logger.LogError("Invalid payload: PdfS3Key is missing for GeneratePaymentZip.");
                return;
            }
            await _service.GeneratePaymentZipAsync(payload, context.Logger);
        }
        else if (payload.Action == "WatermarkPaymentAndZip")
        {
            // Stamp the CRP PDF and update InvoicePayment collection (not Invoice)
            if (string.IsNullOrWhiteSpace(payload.PdfS3Key))
            {
                context.Logger.LogError("Invalid payload: PdfS3Key is missing for WatermarkPaymentAndZip.");
                return;
            }
            await _service.StampPaymentAndSaveAsync(payload, context.Logger);
        }
        else
        {
            // Default action: WatermarkAndZip (stamp cancelled PDF + create cancelled ZIP + send email)
            if (string.IsNullOrWhiteSpace(payload.PdfS3Key))
            {
                context.Logger.LogError("Invalid payload: PdfS3Key is missing for WatermarkAndZip.");
                return;
            }
            await _service.StampAndSaveAsync(payload, context.Logger);
        }

        context.Logger.LogInformation("PdfWatermarkLambda completed.");
    }

    private static string GetRequiredEnv(string name)
        => Environment.GetEnvironmentVariable(name)
           ?? throw new InvalidOperationException($"Required env var '{name}' is not set.");

    private static string Env(string name, string fallback)
        => Environment.GetEnvironmentVariable(name) ?? fallback;
}
