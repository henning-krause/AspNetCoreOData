//-----------------------------------------------------------------------------
// <copyright file="ODataCollectionSerializer.cs" company=".NET Foundation">
//      Copyright (c) .NET Foundation and Contributors. All rights reserved.
//      See License.txt in the project root for license information.
// </copyright>
//------------------------------------------------------------------------------

using System;
using System.Collections;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.OData.Abstracts;
using Microsoft.AspNetCore.OData.Extensions;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.OData;
using Microsoft.OData.Edm;

namespace Microsoft.AspNetCore.OData.Formatter.Serialization
{
    /// <summary>
    /// ODataSerializer for serializing collection of primitive or enum types.
    /// </summary>
    public class ODataCollectionSerializer : ODataEdmTypeSerializer
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ODataCollectionSerializer"/> class.
        /// </summary>
        /// <param name="serializerProvider">The serializer provider to use to serialize nested objects.</param>
        public ODataCollectionSerializer(IODataSerializerProvider serializerProvider)
            : base(ODataPayloadKind.Collection, serializerProvider)
        {
        }

        /// <inheritdoc/>
        public override async Task WriteObjectAsync(object graph, Type type, ODataMessageWriter messageWriter,
            ODataSerializerContext writeContext)
        {
            if (messageWriter == null)
            {
                throw Error.ArgumentNull(nameof(messageWriter));
            }

            if (writeContext == null)
            {
                throw Error.ArgumentNull(nameof(writeContext));
            }

            IEdmTypeReference collectionType = writeContext.GetEdmType(graph, type);
            Contract.Assert(collectionType != null);

            IEdmTypeReference elementType = GetElementType(collectionType);
            ODataCollectionWriter writer = await messageWriter.CreateODataCollectionWriterAsync(elementType)
                .ConfigureAwait(false);
            await WriteCollectionAsync(writer, graph, collectionType.AsCollection(), writeContext).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public sealed override ODataValue CreateODataValue(object graph, IEdmTypeReference expectedType,
            ODataSerializerContext writeContext)
        {
            IEnumerable enumerable = graph as IEnumerable;
            if (enumerable == null && graph != null)
            {
                throw Error.Argument(nameof(graph), SRResources.ArgumentMustBeOfType, typeof(IEnumerable).Name);
            }

            if (expectedType == null)
            {
                throw Error.ArgumentNull(nameof(expectedType));
            }

            IEdmTypeReference elementType = GetElementType(expectedType);
            return CreateODataCollectionValue(enumerable, elementType, writeContext);
        }

        /// <summary>
        /// Writes the given <paramref name="graph"/> using the given <paramref name="writer"/>.
        /// </summary>
        /// <param name="writer">The <see cref="ODataCollectionWriter"/> to use.</param>
        /// <param name="graph">The collection to write.</param>
        /// <param name="collectionType">The EDM type of the collection.</param>
        /// <param name="writeContext">The serializer context.</param>
        public virtual async Task WriteCollectionAsync(ODataCollectionWriter writer, object graph, IEdmTypeReference collectionType,
            ODataSerializerContext writeContext)
        {
            if (writer == null)
            {
                throw Error.ArgumentNull(nameof(writer));
            }

            if (writeContext == null)
            {
                throw Error.ArgumentNull(nameof(writeContext));
            }

            ODataCollectionStart collectionStart = new ODataCollectionStart { Name = writeContext.RootElementName };

            await writer.WriteStartAsync(collectionStart).ConfigureAwait(false);

            if (graph != null)
            {
                ODataCollectionValue collectionValue = CreateODataValue(graph, collectionType, writeContext) as ODataCollectionValue;
                if (collectionValue != null)
                {
                    foreach (object item in collectionValue.Items)
                    {
                        await writer.WriteItemAsync(item).ConfigureAwait(false);
                    }
                }
            }

            await writer.WriteEndAsync().ConfigureAwait(false);

            if (writeContext.Request != null)
            {
                if (writeContext.Request.ODataFeature() is ODataFeature odataFeature)
                {
                    if (odataFeature.NextLink != null)
                    {
                        collectionStart.NextPageLink = odataFeature.NextLink;
                    }
                    else if (odataFeature.QueryOptions != null)
                    {
                        // Collection serializer is called only for collection of primitive values - A null object will be supplied since it is a non-entity value
                        SkipTokenHandler skipTokenHandler = writeContext.QueryOptions.Context.GetSkipTokenHandler();
                        collectionStart.NextPageLink = skipTokenHandler.GenerateNextPageLink(
                            new Uri(writeContext.Request.GetEncodedUrl()), odataFeature.PageSize(), null, writeContext);
                    }

                    if (odataFeature.TotalCount != null)
                    {
                        collectionStart.Count = odataFeature.TotalCount;
                    }
                }
            }

        }

        /// <summary>
        /// Creates an <see cref="ODataCollectionValue"/> for the enumerable represented by <paramref name="enumerable"/>.
        /// </summary>
        /// <param name="enumerable">The value of the collection to be created.</param>
        /// <param name="elementType">The element EDM type of the collection.</param>
        /// <param name="writeContext">The serializer context to be used while creating the collection.</param>
        /// <returns>The created <see cref="ODataCollectionValue"/>.</returns>
        public virtual ODataCollectionValue CreateODataCollectionValue(IEnumerable enumerable, IEdmTypeReference elementType,
            ODataSerializerContext writeContext)
        {
            if (elementType == null)
            {
                throw Error.ArgumentNull(nameof(elementType));
            }

            if (writeContext == null)
            {
                throw Error.ArgumentNull(nameof(writeContext));
            }

            ArrayList valueCollection = new ArrayList();

            if (enumerable != null)
            {
                IODataEdmTypeSerializer itemSerializer = null;
                foreach (object item in enumerable)
                {
                    if (item == null)
                    {
                        if (elementType.IsNullable)
                        {
                            valueCollection.Add(value: null);
                            continue;
                        }

                        throw new SerializationException(SRResources.NullElementInCollection);
                    }

                    IEdmTypeReference actualType = writeContext.GetEdmType(item, item.GetType());
                    Contract.Assert(actualType != null);

                    itemSerializer = itemSerializer ?? SerializerProvider.GetEdmTypeSerializer(actualType);
                    if (itemSerializer == null)
                    {
                        throw new SerializationException(
                            Error.Format(SRResources.TypeCannotBeSerialized, actualType.FullName()));
                    }

                    // ODataCollectionWriter expects the individual elements in the collection to be the underlying
                    // values and not ODataValues.
                    valueCollection.Add(
                        itemSerializer.CreateODataValue(item, actualType, writeContext).GetInnerValue());
                }
            }

            // Ideally, we'd like to do this:
            // string typeName = _edmCollectionType.FullName();
            // But ODataLib currently doesn't support .FullName() for collections. As a workaround, we construct the
            // collection type name the hard way.
            string typeName = "Collection(" + elementType.FullName() + ")";

            // ODataCollectionValue is only a V3 property, arrays inside Complex Types or Entity types are only supported in V3
            // if a V1 or V2 Client requests a type that has a collection within it ODataLib will throw.
            ODataCollectionValue value = new ODataCollectionValue
            {
                Items = valueCollection.Cast<object>(),
                TypeName = typeName
            };

            AddTypeNameAnnotationAsNeeded(value, writeContext.MetadataLevel);
            return value;
        }

        /// <summary>
        /// Adds the type name annotations required for proper json light serialization.
        /// </summary>
        /// <param name="value">The collection value for which the annotations have to be added.</param>
        /// <param name="metadataLevel">The OData metadata level of the response.</param>
        protected internal static void AddTypeNameAnnotationAsNeeded(ODataCollectionValue value, ODataMetadataLevel metadataLevel)
        {
            // ODataLib normally has the caller decide whether or not to serialize properties by leaving properties
            // null when values should not be serialized. The TypeName property is different and should always be
            // provided to ODataLib to enable model validation. A separate annotation is used to decide whether or not
            // to serialize the type name (a null value prevents serialization).
            Contract.Assert(value != null);

            // Only add an annotation if we want to override ODataLib's default type name serialization behavior.
            if (ShouldAddTypeNameAnnotation(metadataLevel))
            {
                string typeName;

                // Provide the type name to serialize (or null to force it not to serialize).
                if (ShouldSuppressTypeNameSerialization(metadataLevel))
                {
                    typeName = null;
                }
                else
                {
                    typeName = value.TypeName;
                }

                value.TypeAnnotation = new ODataTypeAnnotation(typeName);
            }
        }

        internal static bool ShouldAddTypeNameAnnotation(ODataMetadataLevel metadataLevel)
        {
            switch (metadataLevel)
            {
                // For collections, the default behavior matches the requirements for minimal metadata mode, so no
                // annotation is necessary.
                case ODataMetadataLevel.Minimal:
                    return false;
                // In other cases, this class must control the type name serialization behavior.
                case ODataMetadataLevel.Full:
                case ODataMetadataLevel.None:
                default: // All values already specified; just keeping the compiler happy.
                    return true;
            }
        }

        internal static bool ShouldSuppressTypeNameSerialization(ODataMetadataLevel metadataLevel)
        {
            Contract.Assert(metadataLevel != ODataMetadataLevel.Minimal);

            switch (metadataLevel)
            {
                case ODataMetadataLevel.None:
                    return true;
                case ODataMetadataLevel.Full:
                default: // All values already specified; just keeping the compiler happy.
                    return false;
            }
        }

        internal static IEdmTypeReference GetElementType(IEdmTypeReference feedType)
        {
            if (feedType.IsCollection())
            {
                return feedType.AsCollection().ElementType();
            }

            string message = Error.Format(SRResources.CannotWriteType, typeof(ODataCollectionSerializer).Name, feedType.FullName());
            throw new SerializationException(message);
        }
    }
}
