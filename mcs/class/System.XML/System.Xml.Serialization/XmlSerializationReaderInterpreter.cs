//
// XmlSerializationReaderInterpreter.cs: 
//
// Author:
//   Lluis Sanchez Gual (lluis@ximian.com)
//
// (C) 2002, 2003 Ximian, Inc.  http://www.ximian.com
//

using System;
using System.Reflection;
using System.Collections;

namespace System.Xml.Serialization
{
	internal class XmlSerializationReaderInterpreter: XmlSerializationReader
	{
		XmlMapping _typeMap;
		SerializationFormat _format;

		public XmlSerializationReaderInterpreter(XmlMapping typeMap)
		{
			_typeMap = typeMap;
			_format = typeMap.Format;
		}

		protected override void InitCallbacks ()
		{
			ArrayList maps = _typeMap.RelatedMaps;
			if (maps != null)
			{
				foreach (XmlTypeMapping map in maps)  
				{
					if (map.TypeData.SchemaType == SchemaTypes.Class || map.TypeData.SchemaType == SchemaTypes.Enum)
					{
						ReaderCallbackInfo info = new ReaderCallbackInfo (this, map);
						AddReadCallback (map.XmlType, map.Namespace, map.TypeData.Type, new XmlSerializationReadCallback (info.ReadObject));
					}
				}
			}
		}

		protected override void InitIDs ()
		{
		}

		protected XmlTypeMapping GetTypeMap (Type type)
		{
			ArrayList maps = _typeMap.RelatedMaps;
			if (maps != null)
			{
				foreach (XmlTypeMapping map in maps)
					if (map.TypeData.Type == type) return map;
			}
			throw new InvalidOperationException ("Type " + type + " not mapped");
		}

		public object ReadObject ()
		{
			Reader.MoveToContent();
			if (_typeMap is XmlTypeMapping)
			{
				if (_format == SerializationFormat.Literal)
					return ReadRoot ((XmlTypeMapping)_typeMap);
				else
					return ReadEncodedObject ((XmlTypeMapping)_typeMap);
			}
			else
				return ReadMessage ((XmlMembersMapping)_typeMap);
		}

		object ReadEncodedObject (XmlTypeMapping typeMap)
		{
			object ob = null;
			Reader.MoveToContent();
			if (Reader.NodeType == System.Xml.XmlNodeType.Element) 
			{
				if (Reader.LocalName == typeMap.ElementName && Reader.NamespaceURI == typeMap.Namespace)
					ob = ReadReferencedElement();
				else 
					throw CreateUnknownNodeException();
			}
			else 
				UnknownNode(null);

			ReadReferencedElements();
			return ob;
		}

		protected virtual object ReadMessage (XmlMembersMapping typeMap)
		{
			object[] parameters = new object[typeMap.Count];

			if (typeMap.HasWrapperElement)
			{
				if (_format == SerializationFormat.Encoded)
				{
					while (Reader.NodeType == System.Xml.XmlNodeType.Element)
					{
						string root = Reader.GetAttribute ("root", XmlSerializer.EncodingNamespace);
						if (root == null || System.Xml.XmlConvert.ToBoolean(root)) break;
						ReadReferencedElement ();
						Reader.MoveToContent ();
					}
				}
				
				while (Reader.NodeType != System.Xml.XmlNodeType.EndElement) 
				{
					if (Reader.IsStartElement(typeMap.ElementName, typeMap.Namespace) 
					    || _format == SerializationFormat.Encoded)  
					{
						if (Reader.IsEmptyElement) { Reader.Skip(); Reader.MoveToContent(); continue; }
						Reader.ReadStartElement();
						ReadMembers ((ClassMap)typeMap.ObjectMap, parameters, true, false);
						ReadEndElement();
						break;
					}
					else 
						UnknownNode(null);

					Reader.MoveToContent();
				}
			}
			else
				ReadMembers ((ClassMap)typeMap.ObjectMap, parameters, true, _format == SerializationFormat.Encoded);

			if (_format == SerializationFormat.Encoded)
				ReadReferencedElements();

			return parameters;
		}

		object ReadRoot (XmlTypeMapping rootMap)
		{
			if (rootMap.TypeData.SchemaType == SchemaTypes.XmlNode)
			{
				return ReadXmlNodeElement (rootMap, true);
			}
			else
			{
				if (Reader.LocalName != rootMap.ElementName || Reader.NamespaceURI != rootMap.Namespace)
					throw CreateUnknownNodeException();
				
				return ReadObject (rootMap, true, true);
			}
		}		


		protected virtual object ReadObject (XmlTypeMapping typeMap, bool isNullable, bool checkType)
		{
			switch (typeMap.TypeData.SchemaType)
			{
				case SchemaTypes.Class: return ReadClassInstance (typeMap, isNullable, checkType);
				case SchemaTypes.Array: return ReadListElement (typeMap, isNullable, null, true);
				case SchemaTypes.XmlNode: return ReadXmlNodeElement (typeMap, isNullable);
				case SchemaTypes.Primitive: return ReadPrimitiveElement (typeMap, isNullable);
				case SchemaTypes.Enum: return ReadEnumElement (typeMap, isNullable);
				case SchemaTypes.XmlSerializable: return ReadXmlSerializableElement (typeMap, isNullable);
				default: throw new Exception ("Unsupported map type");
			}
		}

		protected virtual object ReadClassInstance (XmlTypeMapping typeMap, bool isNullable, bool checkType)
		{
			if (isNullable && ReadNull()) return null;

			if (checkType) 
			{
                System.Xml.XmlQualifiedName t = GetXsiType();
				if (t != null) 
				{
					XmlTypeMapping realMap = typeMap.GetRealElementMap (t.Name, t.Namespace);
					if (realMap == null) {
						if (typeMap.TypeData.Type == typeof(object))
							return ReadTypedPrimitive (t);
						else
							throw CreateUnknownTypeException((System.Xml.XmlQualifiedName)t);
					}
					if (realMap != typeMap)
						return ReadObject (realMap, false, false);
				}
            }

			object ob = Activator.CreateInstance (typeMap.TypeData.Type);

			Reader.MoveToElement();
			bool isEmpty = Reader.IsEmptyElement;
			ReadClassInstanceMembers (typeMap, ob);

			if (isEmpty) Reader.Skip();
			else ReadEndElement();

			return ob;
		}

		protected virtual void ReadClassInstanceMembers (XmlTypeMapping typeMap, object ob)
		{
			ReadMembers ((ClassMap) typeMap.ObjectMap, ob, false, false);
		}

		void ReadMembers (ClassMap map, object ob, bool isValueList, bool readByOrder)
		{
			if (map.AttributeMembers != null)
			{
				// Reads attributes

				XmlTypeMapMember anyAttrMember = map.DefaultAnyAttributeMember;
				int anyAttributeIndex = 0;
				object anyAttributeArray = null;

				while (Reader.MoveToNextAttribute())
				{
					XmlTypeMapMemberAttribute member = map.GetAttribute (Reader.LocalName, Reader.NamespaceURI);

					if (member != null) 
					{
						SetMemberValue (member, ob, GetValueFromXmlString (Reader.Value, member.TypeData, member.MappedType), isValueList);
					}
					else if (IsXmlnsAttribute(Reader.Name)) 
					{
						// If the map has NamespaceDeclarations,
						// then store this xmlns to the given member.
						// If the instance doesn't exist, then create.
						if (map.NamespaceDeclarations != null) {
							XmlSerializerNamespaces nss = this.GetMemberValue (map.NamespaceDeclarations, ob, isValueList) as XmlSerializerNamespaces;
							if (nss == null) {
								nss = new XmlSerializerNamespaces ();
								SetMemberValue (map.NamespaceDeclarations, ob, nss, isValueList);
							}
							if (Reader.Prefix == "xmlns")
								nss.Add (Reader.LocalName, Reader.Value);
							else
								nss.Add ("", Reader.Value);
						}
					}	
					else if (anyAttrMember != null) 
					{
						XmlAttribute attr = (XmlAttribute) Document.ReadNode(Reader);
						ParseWsdlArrayType (attr);
						AddListValue (anyAttrMember.TypeData, ref anyAttributeArray, anyAttributeIndex++, attr, true);
					}
					else
						ProcessUnknownAttribute(ob);
				}

				if (anyAttrMember != null)
				{
					anyAttributeArray = ShrinkArray ((Array)anyAttributeArray, anyAttributeIndex, anyAttrMember.TypeData.Type.GetElementType(), true);
					SetMemberValue (anyAttrMember, ob, anyAttributeArray, isValueList);
				}
			}
			
			if (!isValueList)
			{
				Reader.MoveToElement();
				if (Reader.IsEmptyElement) 
					return;

				Reader.ReadStartElement();
			}

			// Reads elements

			bool[] readFlag = new bool[(map.ElementMembers != null) ? map.ElementMembers.Count : 0];

			Reader.MoveToContent();

			int[] indexes = null;
			object[] flatLists = null;
			Fixup fixup = null;
			int ind = 0;
			int maxInd;

			if (readByOrder) {
				if (map.ElementMembers != null) maxInd = map.ElementMembers.Count;
				else maxInd = 0;
			}
			else
				maxInd = int.MaxValue;

			if (map.FlatLists != null) 
			{
				indexes = new int[map.FlatLists.Count];
				flatLists = new object[map.FlatLists.Count];
				foreach (XmlTypeMapMemberExpandable mem in map.FlatLists)
					if (IsReadOnly (mem, ob, isValueList)) flatLists[mem.FlatArrayIndex] = mem.GetValue (ob);
			}
			
			if (_format == SerializationFormat.Encoded && map.ElementMembers != null)
			{
				FixupCallbackInfo info = new FixupCallbackInfo (this, map, isValueList);
				fixup = new Fixup(ob, new XmlSerializationFixupCallback(info.FixupMembers), map.ElementMembers.Count);
				AddFixup (fixup);
				if (readByOrder) maxInd = map.ElementMembers.Count;
			}

			while (Reader.NodeType != System.Xml.XmlNodeType.EndElement && (ind < maxInd)) 
			{
				if (Reader.NodeType == System.Xml.XmlNodeType.Element) 
				{
					XmlTypeMapElementInfo info = !readByOrder ? map.GetElement (Reader.LocalName, Reader.NamespaceURI) : map.GetElement (ind++);
					if (info != null && !readFlag[info.Member.Index] )
					{
						if (info.Member.GetType() == typeof (XmlTypeMapMemberList))
						{
							if (_format == SerializationFormat.Encoded && info.MultiReferenceType)
							{
								object list = ReadReferencingElement (out fixup.Ids[info.Member.Index]);
								if (fixup.Ids[info.Member.Index] == null)	// Already read
								{
									if (IsReadOnly (info.Member, ob, isValueList)) throw CreateReadOnlyCollectionException (info.TypeData.FullTypeName);
									else SetMemberValue (info.Member, ob, list, isValueList);
								}
								else if (!info.MappedType.TypeData.Type.IsArray)
								{
									if (IsReadOnly (info.Member, ob, isValueList)) 
										list = GetMemberValue (info.Member, ob, isValueList);
									else { 
										list = CreateList (info.MappedType.TypeData.Type);
										SetMemberValue (info.Member, ob, list, isValueList);
									}
									AddFixup (new CollectionFixup (list, new XmlSerializationCollectionFixupCallback (FillList), fixup.Ids[info.Member.Index]));
									fixup.Ids[info.Member.Index] = null;	// The member already has the value, no further fix needed.
								}
							}
							else
							{
								if (IsReadOnly (info.Member, ob, isValueList)) ReadListElement (info.MappedType, info.IsNullable, GetMemberValue (info.Member, ob, isValueList), false);
								else SetMemberValue (info.Member, ob, ReadListElement (info.MappedType, info.IsNullable, null, true), isValueList);
							}
							readFlag[info.Member.Index] = true;
						}
						else if (info.Member.GetType() == typeof (XmlTypeMapMemberFlatList))
						{
							XmlTypeMapMemberFlatList mem = (XmlTypeMapMemberFlatList)info.Member;
							AddListValue (mem.TypeData, ref flatLists[mem.FlatArrayIndex], indexes[mem.FlatArrayIndex]++, ReadObjectElement (info), !IsReadOnly (info.Member, ob, isValueList));
						}
						else if (info.Member.GetType() == typeof (XmlTypeMapMemberAnyElement))
						{
							XmlTypeMapMemberAnyElement mem = (XmlTypeMapMemberAnyElement)info.Member;
							if (mem.TypeData.IsListType) AddListValue (mem.TypeData, ref flatLists[mem.FlatArrayIndex], indexes[mem.FlatArrayIndex]++, ReadXmlNode (false), true);
							else SetMemberValue (mem, ob, ReadXmlNode (false), isValueList);
						}
						else if (info.Member.GetType() == typeof(XmlTypeMapMemberElement))
						{
							object val;
							readFlag[info.Member.Index] = true;
							if (_format == SerializationFormat.Encoded && info.MultiReferenceType) 
							{
								val = ReadReferencingElement (out fixup.Ids[info.Member.Index]);
								if (fixup.Ids[info.Member.Index] == null)	// already read
									SetMemberValue (info.Member, ob, val, isValueList);
							}
							else 
								SetMemberValue (info.Member, ob, ReadObjectElement (info), isValueList);
						}
						else
							throw new InvalidOperationException ("Unknown member type");
					}
					else if (map.DefaultAnyElementMember != null)
					{
						XmlTypeMapMemberAnyElement mem = map.DefaultAnyElementMember;
						if (mem.TypeData.IsListType) AddListValue (mem.TypeData, ref flatLists[mem.FlatArrayIndex], indexes[mem.FlatArrayIndex]++, ReadXmlNode (false), true);
						else SetMemberValue (mem, ob, ReadXmlNode (false), isValueList);
					}
					else 
						ProcessUnknownElement(ob);
				}
				else if (Reader.NodeType == System.Xml.XmlNodeType.Text && map.XmlTextCollector != null)
				{
					if (map.XmlTextCollector is XmlTypeMapMemberExpandable)
					{
						XmlTypeMapMemberExpandable mem = (XmlTypeMapMemberExpandable)map.XmlTextCollector;
						XmlTypeMapMemberFlatList flatl = mem as XmlTypeMapMemberFlatList;
						Type itype = (flatl == null) ? mem.TypeData.ListItemType : flatl.ListMap.FindTextElement().TypeData.Type;

						object val = (itype == typeof (string)) ? (object) Reader.ReadString() : (object) ReadXmlNode (false);
						AddListValue (mem.TypeData, ref flatLists[mem.FlatArrayIndex], indexes[mem.FlatArrayIndex]++, val, true);
					}
					else
					{
						XmlTypeMapMemberElement mem = (XmlTypeMapMemberElement) map.XmlTextCollector;
						XmlTypeMapElementInfo info = (XmlTypeMapElementInfo) mem.ElementInfo [0];
						if (info.TypeData.Type == typeof (string))
							SetMemberValue (mem, ob, ReadString ((string) GetMemberValue (mem, ob, isValueList)), isValueList);
						else
							SetMemberValue (mem, ob, GetValueFromXmlString (Reader.ReadString(), info.TypeData, info.MappedType), isValueList);
					}
				}
				else 
					UnknownNode(ob);

				Reader.MoveToContent();
			}

			if (flatLists != null)
			{
				foreach (XmlTypeMapMemberExpandable mem in map.FlatLists)
				{
					Object list = flatLists[mem.FlatArrayIndex];
					if (mem.TypeData.Type.IsArray)
						list = ShrinkArray ((Array)list, indexes[mem.FlatArrayIndex], mem.TypeData.Type.GetElementType(), true);
					if (!IsReadOnly (mem, ob, isValueList))
						SetMemberValue (mem, ob, list, isValueList);
				}
			}		
		}

		internal void FixupMembers (ClassMap map, object obfixup, bool isValueList)
		{
			Fixup fixup = (Fixup)obfixup;
			ICollection members = map.ElementMembers;
			string[] ids = fixup.Ids;
			foreach (XmlTypeMapMember member in members)
			{
				if (ids[member.Index] != null)
					SetMemberValue (member, fixup.Source, GetTarget(ids[member.Index]), isValueList);
			}
		}
		
		protected virtual void ProcessUnknownAttribute (object target)
		{
			UnknownNode (target);
		}
		
		protected virtual void ProcessUnknownElement (object target)
		{
			UnknownNode (target);
		}

		bool IsReadOnly (XmlTypeMapMember member, object ob, bool isValueList)
		{
			if (isValueList) return false;
			else return member.IsReadOnly (ob.GetType());
		}

		void SetMemberValue (XmlTypeMapMember member, object ob, object value, bool isValueList)
		{
			if (isValueList) ((object[])ob)[member.Index] = value;
			else {
				member.SetValue (ob, value);
				if (member.IsOptionalValueType)
					member.SetValueSpecified (ob, true); 
			}
		}

		object GetMemberValue (XmlTypeMapMember member, object ob, bool isValueList)
		{
			if (isValueList) return ((object[])ob)[member.Index];
			else return member.GetValue (ob);
		}

		object ReadObjectElement (XmlTypeMapElementInfo elem)
		{
			switch (elem.TypeData.SchemaType)
			{
				case SchemaTypes.XmlNode:
					return ReadXmlNode (true);

				case SchemaTypes.Primitive:
				case SchemaTypes.Enum:
					return ReadPrimitiveValue (elem);

				case SchemaTypes.Array:
					return ReadListElement (elem.MappedType, elem.IsNullable, null, true);

				case SchemaTypes.Class:
					return ReadObject (elem.MappedType, elem.IsNullable, true);

				case SchemaTypes.XmlSerializable:
					object ob = Activator.CreateInstance (elem.TypeData.Type);
					return ReadSerializable ((IXmlSerializable)ob);

				default:
					throw new NotSupportedException ("Invalid value type");
			}
		}

		object ReadPrimitiveValue (XmlTypeMapElementInfo elem)
		{
			if (elem.TypeData.Type == typeof (XmlQualifiedName)) {
				if (elem.IsNullable) return ReadNullableQualifiedName ();
				else return ReadElementQualifiedName ();
			}
			else if (elem.IsNullable)
				return GetValueFromXmlString (ReadNullableString (), elem.TypeData, elem.MappedType);
			else
				return GetValueFromXmlString (Reader.ReadElementString (), elem.TypeData, elem.MappedType);
		}
		
		object GetValueFromXmlString (string value, TypeData typeData, XmlTypeMapping typeMap)
		{
			if (typeData.SchemaType == SchemaTypes.Array)
				return ReadListString (typeMap, value);
			else if (typeData.SchemaType == SchemaTypes.Enum)
				return GetEnumValue (typeMap, value);
			else if (typeData.Type == typeof (XmlQualifiedName))
				return ToXmlQualifiedName (value);
			else 
				return XmlCustomFormatter.FromXmlString (typeData, value);
		}

		object ReadListElement (XmlTypeMapping typeMap, bool isNullable, object list, bool canCreateInstance)
		{
			Type listType = typeMap.TypeData.Type;
			ListMap listMap = (ListMap)typeMap.ObjectMap;

			if (ReadNull()) return null;

			if (list == null) {
				if (canCreateInstance) list = CreateList (listType);
				else throw CreateReadOnlyCollectionException (typeMap.TypeFullName);
			}

			if (Reader.IsEmptyElement) {
				Reader.Skip();
				if (listType.IsArray)
					list = ShrinkArray ((Array)list, 0, listType.GetElementType(), isNullable);
				return list;
			}

			int index = 0;
			Reader.ReadStartElement();
			Reader.MoveToContent();

			while (Reader.NodeType != System.Xml.XmlNodeType.EndElement) 
			{
				if (Reader.NodeType == System.Xml.XmlNodeType.Element) 
				{
					XmlTypeMapElementInfo elemInfo = listMap.FindElement (Reader.LocalName, Reader.NamespaceURI);
					if (elemInfo != null)
						AddListValue (typeMap.TypeData, ref list, index++, ReadObjectElement (elemInfo), false);
					else
						UnknownNode(null);
				}
				else 
					UnknownNode(null);

				Reader.MoveToContent();
			}
			ReadEndElement();

			if (listType.IsArray)
				list = ShrinkArray ((Array)list, index, listType.GetElementType(), isNullable);

			return list;
		}

		object ReadListString (XmlTypeMapping typeMap, string values)
		{
			Type listType = typeMap.TypeData.Type;
			ListMap listMap = (ListMap)typeMap.ObjectMap;
			values = values.Trim ();

			if (values == string.Empty)
			{
				return Array.CreateInstance (listType.GetElementType(), 0);
			}

			string[] valueArray = values.Split (' ');
			Array list = Array.CreateInstance (listType.GetElementType(), valueArray.Length);

			XmlTypeMapElementInfo info = (XmlTypeMapElementInfo)listMap.ItemInfo[0];

			for (int index = 0; index < valueArray.Length; index++)
				list.SetValue (GetValueFromXmlString (valueArray[index], info.TypeData, info.MappedType), index);

			return list;
		}

		void AddListValue (TypeData listType, ref object list, int index, object value, bool canCreateInstance)
		{
			Type type = listType.Type;
			if (type.IsArray)
			{
				list = EnsureArrayIndex ((Array)list, index, type.GetElementType());
				((Array)list).SetValue (value, index);
			}
			else	// Must be IEnumerable
			{
				if (list == null) {
					if (canCreateInstance) list = Activator.CreateInstance (type);
					else throw CreateReadOnlyCollectionException (type.FullName);
				}

				MethodInfo mi = type.GetMethod ("Add", new Type[] {listType.ListItemType} );
				mi.Invoke (list, new object[] { value });
			}
		}

		object CreateList (Type listType)
		{
			if (listType.IsArray)
				return EnsureArrayIndex (null, 0, listType.GetElementType());
			else
				return Activator.CreateInstance (listType);
		}

		void FillList (object list, object items)
		{
			CopyEnumerableList (items, list);
		}

		void CopyEnumerableList (object source, object dest)
		{
			if (dest == null) throw CreateReadOnlyCollectionException (source.GetType().FullName);

			object[] param = new object[1];
			MethodInfo mi = dest.GetType().GetMethod ("Add");
			foreach (object ob in (IEnumerable)source)
			{
				param[0] = ob;
				mi.Invoke (dest, param);
			}
		}

		int GetListCount (TypeData listType, object ob)
		{
			if (listType.Type.IsArray)
				return ((Array)ob).Length;
			else
				return (int) listType.Type.GetProperty ("Count").GetValue(ob,null);
		}

		object ReadXmlNodeElement (XmlTypeMapping typeMap, bool isNullable)
		{
			return ReadXmlNode (false);
		}

		object ReadPrimitiveElement (XmlTypeMapping typeMap, bool isNullable)
		{
			XmlQualifiedName t = GetXsiType();
			if (t == null) t = new XmlQualifiedName (typeMap.XmlType, typeMap.Namespace);
			return ReadTypedPrimitive (t);
		}

		object ReadEnumElement (XmlTypeMapping typeMap, bool isNullable)
		{
			Reader.ReadStartElement ();
			object o = GetEnumValue (typeMap, Reader.ReadString());
			Reader.ReadEndElement ();
			return o;
		}

		object GetEnumValue (XmlTypeMapping typeMap, string val)
		{
			EnumMap map = (EnumMap) typeMap.ObjectMap;
			string ev = map.GetEnumName (val);
			if (ev == null) throw CreateUnknownConstantException (val, typeMap.TypeData.Type);
			return Enum.Parse (typeMap.TypeData.Type, ev);
		}

		object ReadXmlSerializableElement (XmlTypeMapping typeMap, bool isNullable)
		{
			Reader.MoveToContent ();
			if (Reader.NodeType == XmlNodeType.Element)
			{
				if (Reader.LocalName == typeMap.ElementName && Reader.NamespaceURI == typeMap.Namespace)
				{
					object ob = Activator.CreateInstance (typeMap.TypeData.Type);
					return ReadSerializable ((IXmlSerializable)ob);
				}
				else
					throw CreateUnknownNodeException ();
			}
			else
			{
				UnknownNode (null);
				return null;
			}
		}

		class FixupCallbackInfo
		{
			XmlSerializationReaderInterpreter _sri;
			ClassMap _map;
			bool _isValueList;

			public FixupCallbackInfo (XmlSerializationReaderInterpreter sri, ClassMap map, bool isValueList)
			{
				_sri = sri;
				_map = map;
				_isValueList = isValueList;
			}

			public void FixupMembers (object fixup)
			{
				_sri.FixupMembers (_map, fixup, _isValueList);
			}
		}

		class ReaderCallbackInfo
		{
			XmlSerializationReaderInterpreter _sri;
			XmlTypeMapping _typeMap;

			public ReaderCallbackInfo (XmlSerializationReaderInterpreter sri, XmlTypeMapping typeMap)
			{
				_sri = sri;
				_typeMap = typeMap;
			}

			internal object ReadObject ()
			{
				return _sri.ReadObject (_typeMap, true, true);
			}
		}
	}
}
