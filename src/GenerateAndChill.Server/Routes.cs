﻿using Azure;
using Azure.AI.OpenAI;
using Azure.Data.Tables;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using Microsoft.AspNetCore.Mvc;

namespace GenerateAndChill.Server;

public static class Routes
{
    private const string ContainerName = "images";
    static readonly string SystemPrompt = """
        If no style of the image has been provided, use one of the following styles:
        - Realistic
        - Sketch
        - Cartoon
        - Watercolour painting
        - Oil painting

        Use the following prompt to generate the image:

        """;

    public static void MapRoutes(this WebApplication app)
    {
        app.MapPost("/api/image/generate", HandleImageGeneration);
        app.MapGet("/api/image/{id}", HandleImageRetrieval);
    }

    private static async Task<IResult> HandleImageRetrieval([FromServices] TableServiceClient tableClient, [FromServices] BlobServiceClient blobClient, string id)
    {
        TableClient table = tableClient.GetTableClient(ContainerName);
        await table.CreateIfNotExistsAsync();
        Response<TableEntity> response = await table.GetEntityAsync<TableEntity>(id, id);
        TableEntity entity = response.Value;

        if (entity is null)
        {
            return Results.NotFound();
        }

        return Results.Ok(new
        {
            Id = id,
            ImageUri = $"{entity["ImageUri"]}",
            DetailedPrompt = entity["DetailedPrompt"].ToString(),
            OriginalPrompt = entity["OriginalPrompt"].ToString(),
        });
    }

    private static async Task<IResult> HandleImageGeneration(
            [FromServices] OpenAIClient client,
            [FromServices] BlobServiceClient blobClient,
            [FromServices] TableServiceClient tableClient,
            [FromBody] ImageGenerationPayload body)
    {
        string prompt = SystemPrompt + body.Prompt;
        try
        {
            ImageGenerations generations = await GenerateImage(client, prompt);

            if (generations is null || generations.Data.Count != 1)
            {
                return Results.BadRequest("Something caused it to not generate an image");
            }

            Guid id = Guid.NewGuid();

            Uri imageUri = await UploadImage(blobClient, id, generations);
            await StorePrompt(tableClient, prompt, body.Prompt, id, imageUri);

            return Results.Ok(new
            {
                Id = id,
                ImageUri = $"{imageUri.AbsoluteUri}",
                DetailedPrompt = prompt,
                OriginalPrompt = body.Prompt,
            });
        }
        catch (RequestFailedException ex)
        {
            return Results.BadRequest(ex.Message);
        }

    }

    private static async Task StorePrompt(TableServiceClient tableClient, string prompt, string originalPrompt, Guid id, Uri imageUri)
    {
        TableClient table = tableClient.GetTableClient(ContainerName);
        await table.CreateIfNotExistsAsync();
        await table.UpsertEntityAsync(new TableEntity(id.ToString(), id.ToString())
            {
                { "ImageUri", imageUri.ToString() },
                { "DetailedPrompt",  prompt },
                { "OriginalPrompt", originalPrompt }
            });
    }

    private static async Task<ImageGenerations> GenerateImage(OpenAIClient client, string prompt)
    {
        Response<ImageGenerations> response = await client.GetImageGenerationsAsync(new ImageGenerationOptions
        {
            ImageCount = 1,
            Prompt = prompt,
            Size = ImageSize.Size512x512,
            User = "user",
        });

        ImageGenerations generations = response.Value;
        return generations;
    }

    private static async Task<Uri> UploadImage(BlobServiceClient blobClient, Guid id, ImageGenerations generations)
    {
        BlobContainerClient container = blobClient.GetBlobContainerClient(ContainerName);
        await container.CreateIfNotExistsAsync(Azure.Storage.Blobs.Models.PublicAccessType.Blob);
        Uri imageUri = generations.Data[0].Url;
        using HttpClient httpClient = new();
        using Stream imageStream = await httpClient.GetStreamAsync(imageUri);
        BlobClient blob = container.GetBlobClient($"{id}.png");
        await blob.UploadAsync(imageStream, overwrite: true);

        return blob.Uri;
    }
}
