﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using JsonApiSerializer.JsonApi.WellKnown;
using JsonApiSerializer.ReferenceResolvers;
using JsonApiSerializer.Util;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace JsonApiSerializer.JsonConverters
{
    /// <summary>
    /// Provides functionality to convert a JsonApi resoruce object into a .NET object
    /// </summary>
    /// <seealso cref="Newtonsoft.Json.JsonConverter" />
    public partial class ResourceObjectConverter : JsonConverter
    {
        public static Regex DataReadPathRegex = new Regex($@"^$|{PropertyNames.Included}(\[\d+\])?$|{PropertyNames.Data}(\[\d+\])?$");
        public static Regex DataWritePathRegex = new Regex($@"{PropertyNames.Included}(\[\d+\])?$|{PropertyNames.Data}(\[\d+\])?$");

        public override bool CanConvert(Type objectType)
        {
            return TypeInfoShim.GetProperty(objectType.GetTypeInfo(), "Id") != null;
            
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            //we may be starting the deserialization here, if thats the case we need to resolve this object as the root
            object obj;
            if (DocumentRootConverter.TryResolveAsRoot(reader, objectType, serializer, out obj))
                return obj;


            //read into the 'Data' element
            return ReaderUtil.ReadInto(
                reader as ForkableJsonReader ?? new ForkableJsonReader(reader),
                DataReadPathRegex, 
                dataReader=>
            {
                //if the value has been explicitly set to null then the value of the element is simply null
                if (dataReader.TokenType == JsonToken.Null)
                    return null;

                JsonObjectContract contract = (JsonObjectContract)serializer.ContractResolver.ResolveContract(objectType);

                //if we arent given an existing value check the references to see if we have one in there
                //if we dont have one there then create a new object to populate
                if (existingValue == null)
                {
                    var reference = ReaderUtil.ReadAheadToIdentifyObject(dataReader);
                    existingValue = serializer.ReferenceResolver.ResolveReference(null, reference.ToString());
                    if (existingValue == null)
                    {
                        existingValue = contract.DefaultCreator();
                        serializer.ReferenceResolver.AddReference(null, reference.ToString(), existingValue);
                    }
                    if (existingValue is JObject)
                    {
                        //sometimes the value in the reference resolver is a JObject. This occurs when we
                        //did not know what type it should be when we first read it (i.e. included was processed
                        //before the item). In these cases we will create a new object and read data from the JObject
                        dataReader = new ForkableJsonReader(((JObject)existingValue).CreateReader());
                        dataReader.Read(); //JObject readers begin at Not Started
                        existingValue = contract.DefaultCreator();
                        serializer.ReferenceResolver.AddReference(null, reference.ToString(), existingValue);
                    }
                }

                PopulateProperties(serializer, existingValue, dataReader, contract);
                return existingValue;
            });
        }


        protected void PopulateProperties(JsonSerializer serializer, object obj, JsonReader reader, JsonObjectContract contract)
        {
            foreach (var propName in ReaderUtil.IterateProperties(reader))
            {
                var successfullyPopulateProperty = ReaderUtil.TryPopulateProperty(
                    serializer, 
                    obj, 
                    contract.Properties.GetClosestMatchProperty(propName), 
                    reader);

                //flatten out attributes onto the object
                if (!successfullyPopulateProperty && propName == "attributes")
                {
                    foreach (var innerPropName in ReaderUtil.IterateProperties(reader))
                    {
                        ReaderUtil.TryPopulateProperty(
                           serializer,
                           obj,
                           contract.Properties.GetClosestMatchProperty(innerPropName),
                           reader);
                    }
                }

                //flatten out relationships onto the object
                if (!successfullyPopulateProperty && propName == "relationships")
                {
                    foreach (var innerPropName in ReaderUtil.IterateProperties(reader))
                    {
                        ReaderUtil.TryPopulateProperty(
                            serializer, 
                            obj,
                            contract.Properties.GetClosestMatchProperty(innerPropName),
                            reader);
                    }
                }
            }
        }


        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            if (DocumentRootConverter.TryResolveAsRoot(writer, value, serializer))
                return;

            using (WriterUtil.WritePath(writer, DataWritePathRegex, PropertyNames.Data))
            {
                var probe = writer as AttributeOrRelationshipProbe;
                if (probe != null)
                {
                    //if someone is sending a probe its because we are in a relationship property.
                    //let the probe know we are in a relationship and write the reference element
                    probe.PropertyType = AttributeOrRelationshipProbe.Type.Relationship;
                    WriteReferenceObjectJson(writer, value, serializer);
                }
                else
                {
                    WriteFullObjectJson(writer, value, serializer);
                }
            }
        }


        protected void WriteFullObjectJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            var valueType = value.GetType();
            var contract = (JsonObjectContract)serializer.ContractResolver.ResolveContract(valueType);
            writer.WriteStartObject();

            //A resource object MUST contain at least the following top-level members: type
            var typeProp = contract.Properties.GetClosestMatchProperty("type");
            if (typeProp == null)
            {
                writer.WritePropertyName("type");
                serializer.Serialize(writer, typeProp?.ValueProvider?.GetValue(value) ?? valueType.Name.ToLowerInvariant());
            }

            List<JsonWriterCapture> attributes = new List<JsonWriterCapture>();
            List<JsonWriterCapture> relationships = new List<JsonWriterCapture>();
            foreach (var prop in contract.Properties.Where(x=>!x.Ignored))
            {
                var propValue = prop.ValueProvider.GetValue(value);
                if (propValue == null && serializer.NullValueHandling == NullValueHandling.Ignore)
                    continue;

                switch (prop.PropertyName)
                {
                    //In addition, a resource object MAY contain any of these top - level members: links, meta, attributes, relationships
                    case PropertyNames.Links:
                    case PropertyNames.Meta:
                    case PropertyNames.Id: //Id is optional on base objects, so we will put it with the other optional properties
                        writer.WritePropertyName(prop.PropertyName);
                        serializer.Serialize(writer, propValue);
                        break;
                    case PropertyNames.Type:
                        writer.WritePropertyName("type");
                        serializer.Serialize(writer, typeProp?.ValueProvider?.GetValue(value) ?? valueType.Name.ToLowerInvariant());
                        break;
                    default:
                        //we do not know if it is an Attribute or a Relationship
                        //so we will send out a probe to determine which one it is
                        var probe = new AttributeOrRelationshipProbe();
                        probe.WritePropertyName(prop.PropertyName);
                        serializer.Serialize(probe, propValue);

                        (probe.PropertyType == AttributeOrRelationshipProbe.Type.Attribute
                            ? attributes
                            : relationships).Add(probe);
                        break;
                }
            }

            //output our attibutes in an attribute tag
            if (attributes.Count > 0)
            {
                writer.WritePropertyName(PropertyNames.Attributes);
                writer.WriteStartObject();
                foreach (var attribute in attributes)
                    attribute.ApplyCaptured(writer);
                writer.WriteEndObject();
            }

            //output our relationships in a relationship tag
            if (relationships.Count > 0)
            {
                writer.WritePropertyName(PropertyNames.Relationships);
                writer.WriteStartObject();
                foreach (var relationship in relationships)
                    relationship.ApplyCaptured(writer);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();

        }

        protected void WriteReferenceObjectJson(JsonWriter writer, object value, JsonSerializer serializer, JsonObjectContract contract = null)
        {
            contract = contract ?? (JsonObjectContract)serializer.ContractResolver.ResolveContract(value.GetType());

            writer.WriteStartObject();

            //A "resource identifier object" MUST contain type and id members.
            writer.WritePropertyName(PropertyNames.Id);
            var idProp = contract.Properties.GetClosestMatchProperty(PropertyNames.Id);
            var idVal = idProp?.ValueProvider?.GetValue(value) ?? string.Empty;
            serializer.Serialize(writer, idVal);

            writer.WritePropertyName(PropertyNames.Type);
            var typeProp = contract.Properties.GetClosestMatchProperty(PropertyNames.Type);
            var typeVal = typeProp?.ValueProvider?.GetValue(value) ?? value.GetType().Name.ToLowerInvariant();
            serializer.Serialize(writer, typeVal);

            //we will only write the object to included if there are properties that have have data
            //that we cant include wihtin the reference
            var willWriteObjectToIncluded = contract.Properties.Any(prop =>
            {
                //ignore id, type, meta and ignored properties
                if (prop.PropertyName == PropertyNames.Id
                    || prop.PropertyName == PropertyNames.Type
                    || prop.PropertyName == PropertyNames.Meta
                    || prop.Ignored)
                    return false;

                //ignore null properties
                var propValue = prop.ValueProvider.GetValue(value);
                if (propValue == null && serializer.NullValueHandling == NullValueHandling.Ignore)
                    return false;

                //we have another property with a value
                return true;
            });

            //typeically we would just write the meta in the included. But if we are not going to
            //have something in included we will write the meta inline here
            if (!willWriteObjectToIncluded)
            {
                var metaProp = contract.Properties.GetClosestMatchProperty(PropertyNames.Meta);
                var metaVal = metaProp?.ValueProvider?.GetValue(value);
                if (metaVal != null)
                {
                    writer.WritePropertyName(PropertyNames.Meta);
                    serializer.Serialize(writer, metaVal);
                }
            }


            writer.WriteEndObject();


            if (willWriteObjectToIncluded)
            {
                var reference = IncludedReferenceResolver.GetReferenceValue(idVal.ToString(), typeVal.ToString());
                serializer.ReferenceResolver.AddReference(null, reference, value);
            }
        }
    }
}
