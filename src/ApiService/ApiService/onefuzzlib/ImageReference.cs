﻿using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Azure;
using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.Compute;
using Azure.ResourceManager.Compute.Models;
using Compute = Azure.ResourceManager.Compute;

namespace Microsoft.OneFuzz.Service;

[JsonConverter(typeof(Converter<ImageReference>))]
public abstract record ImageReference {
    public static ImageReference MustParse(string image) {
        var result = TryParse(image);
        if (!result.IsOk) {
            var msg = string.Join(", ", result.ErrorV.Errors ?? Array.Empty<string>());
            throw new ArgumentException(msg, nameof(image));
        }

        return result.OkV;
    }

    public static OneFuzzResult<ImageReference> TryParse(string image) {
        ResourceIdentifier identifier;
        ImageReference result;
        try {
            // see if it is a valid ARM resource identifier:
            identifier = new ResourceIdentifier(image);
            if (identifier.ResourceType == GalleryImageResource.ResourceType) {
                result = new GalleryImage(identifier);
            } else if (identifier.ResourceType == ImageResource.ResourceType) {
                result = new Image(identifier);
            } else {
                return new Error(
                    ErrorCode.INVALID_IMAGE,
                    new[] { $"Unknown image resource type: {identifier.ResourceType}" });
            }
        } catch (FormatException) {
            // not an ARM identifier, try to parse a marketplace image:
            var imageParts = image.Split(":");
            // The python code would throw if more than 4 parts are found in the split
            if (imageParts.Length != 4) {
                return new Error(
                    Code: ErrorCode.INVALID_IMAGE,
                    new[] { $"Expected 4 ':' separated parts in '{image}'" });
            }

            result = new Marketplace(
                    Publisher: imageParts[0],
                    Offer: imageParts[1],
                    Sku: imageParts[2],
                    Version: imageParts[3]);
        }

        return OneFuzzResult.Ok(result);
    }

    public abstract Task<OneFuzzResult<Os>> GetOs(ArmClient armClient, string region);

    public abstract Compute.Models.ImageReference ToArm();

    public abstract long MaximumVmCount { get; }

    // Documented here: https://docs.microsoft.com/en-us/azure/virtual-machine-scale-sets/virtual-machine-scale-sets-placement-groups#checklist-for-using-large-scale-sets
    protected const long CustomImageMaximumVmCount = 600;
    protected const long MarketplaceImageMaximumVmCount = 1000;

    public abstract override string ToString();

    [JsonConverter(typeof(Converter<GalleryImage>))]
    public sealed record GalleryImage(ResourceIdentifier Identifier) : ImageReference {
        public override long MaximumVmCount => CustomImageMaximumVmCount;

        public override async Task<OneFuzzResult<Os>> GetOs(ArmClient armClient, string region) {
            try {
                var resource = await armClient.GetGalleryImageResource(Identifier).GetAsync();
                if (resource.Value.Data.OSType is OperatingSystemTypes os) {
                    return OneFuzzResult.Ok(Enum.Parse<Os>(os.ToString(), ignoreCase: true));
                } else {
                    return new Error(ErrorCode.INVALID_IMAGE, new[] { "Specified image had no OSType" });
                }
            } catch (Exception ex) when (ex is RequestFailedException) {
                return new Error(ErrorCode.INVALID_IMAGE, new[] { ex.ToString() });
            }
        }

        public override Compute.Models.ImageReference ToArm()
            => new() { Id = Identifier };

        public override string ToString() => Identifier.ToString();
    }

    [JsonConverter(typeof(Converter<Image>))]
    public sealed record Image(ResourceIdentifier Identifier) : ImageReference {
        public override long MaximumVmCount => CustomImageMaximumVmCount;

        public override async Task<OneFuzzResult<Os>> GetOs(ArmClient armClient, string region) {
            try {
                var resource = await armClient.GetImageResource(Identifier).GetAsync();
                var os = resource.Value.Data.StorageProfile.OSDisk.OSType.ToString();
                return OneFuzzResult.Ok(Enum.Parse<Os>(os.ToString(), ignoreCase: true));
            } catch (Exception ex) when (ex is RequestFailedException) {
                return new Error(ErrorCode.INVALID_IMAGE, new[] { ex.ToString() });
            }
        }

        public override Compute.Models.ImageReference ToArm()
            => new() { Id = Identifier };

        public override string ToString() => Identifier.ToString();
    }

    [JsonConverter(typeof(Converter<Marketplace>))]
    public sealed record Marketplace(
        string Publisher,
        string Offer,
        string Sku,
        string Version) : ImageReference {
        public override long MaximumVmCount => MarketplaceImageMaximumVmCount;

        public override async Task<OneFuzzResult<Os>> GetOs(ArmClient armClient, string region) {
            try {
                var subscription = await armClient.GetDefaultSubscriptionAsync();
                string version;
                if (string.Equals(Version, "latest", StringComparison.Ordinal)) {
                    version =
                        (await subscription.GetVirtualMachineImagesAsync(
                            region,
                            Publisher,
                            Offer,
                            Sku,
                            top: 1
                        ).FirstAsync()).Name;
                } else {
                    version = Version;
                }

                var vm = await subscription.GetVirtualMachineImageAsync(
                    region,
                    Publisher,
                    Offer,
                    Sku,
                    version);

                var os = vm.Value.OSDiskImageOperatingSystem.ToString();
                return OneFuzzResult.Ok(Enum.Parse<Os>(os, ignoreCase: true));
            } catch (RequestFailedException ex) {
                return OneFuzzResult<Os>.Error(
                    ErrorCode.INVALID_IMAGE,
                    ex.ToString()
                );
            }
        }

        public override Compute.Models.ImageReference ToArm() {
            return new() {
                Publisher = Publisher,
                Offer = Offer,
                Sku = Sku,
                Version = Version
            };
        }

        public override string ToString() => string.Join(":", Publisher, Offer, Sku, Version);
    }

    // ImageReference serializes to and from JSON as a string.
    public sealed class Converter<T> : JsonConverter<T> where T : ImageReference {
        public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
            Debug.Assert(typeToConvert.IsAssignableTo(typeof(ImageReference)));

            var value = reader.GetString();
            if (value is null) {
                return null;
            }

            var result = TryParse(value);
            if (!result.IsOk) {
                throw new JsonException(result.ErrorV.Errors?.First());
            }

            return (T)(object)result.OkV;
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.ToString());
    }
}
