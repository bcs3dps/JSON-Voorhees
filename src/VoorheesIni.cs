/*----------------------------------------------------------------------------*/
/* VoorheesIni.cs                                                             */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/*----------------------------------------------------------------------------*/
/* Classic Windows INI files: what an INI line                                */
/* means, how one is written, and INI's answers to the questions the          */
/* dispatcher (VoorheesFormat) asks -- kinds, keys, values, comments,         */
/* labels and warnings.  The line engine (VoorheesLines.cs) does the reading, */
/* writing, rendering and Raw edits, asking this module about single lines.   */
/*                                                                            */
/* THE RULES, as Windows' profile functions read a file (the user's           */
/* decisions INI 1-6):                                                        */
/*     o a line whose first non-space character is ";" or "#" is a comment;   */
/*       there are NO inline comments: a ";" after a value is part of it;     */
/*     o "[" first, and a "]" somewhere after it, starts a section; its name  */
/*       is everything up to the FIRST "]", exactly as written; anything      */
/*       after the "]" is that line's comment (a "[" line without "]" is a    */
/*       key on its own);                                                     */
/*     o otherwise the first "=" splits a line into key and value, spaces     */
/*       round both trimmed (they stay in the file, as the gaps);             */
/*     o a line with no "=" is a key on its own ("key only");                 */
/*     o keys and section names compare without case;                         */
/*     o entries before the first section are document-level ("global");      */
/*     o quoted values are shown and edited exactly as written, quotes        */
/*       included.                                                            */
/* INI is never invalid: any text reads.                                      */
/*                                                                            */
/* How a line maps onto the model is the line engine's (VoorheesLines.cs      */
/* header); here:                                                             */
/*     section : VI_MEMBER, Vi_sKey the name; value a VIN_SECTION container;  */
/*               after the "]": Vi_sOpenGap (spaces) + Vi_sOpenComment (the   */
/*               rest), or Vi_sValueGap when only spaces follow.              */
/*     entry   : VI_MEMBER, Vi_sKey the key; Vi_sKeyGap the spaces, "=" and   */
/*               spaces between key and value; value a VIN_TEXT leaf whose    */
/*               Vn_sText is the value as written (Vn_sRaw stays null: the    */
/*               text IS the token); Vi_sValueGap the trailing spaces.        */
/*     key only: VI_MEMBER with a VIN_KEYONLY leaf (text ""); trailing spaces */
/*               in Vi_sValueGap.                                             */
/*     comment : VI_COMMENT, Vi_sComment the line from its marker on.         */
/*                                                                            */
/* Class:                                                                     */
/*     VoorheesIni (Vin_) : static functions only; nothing is kept between    */
/*                          calls, so any of them may run on a worker thread. */
/*----------------------------------------------------------------------------*/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

/*----------------------------------------------------------------------------*/
/* VoorheesIni                                                                */
/*                                                                            */
/* INI lines, kinds and rules.                                                */
/*----------------------------------------------------------------------------*/
static class VoorheesIni
{
    /*------------------------------------------------------------------------*/
    /* INI's kinds of node, in INI's range (20-29).                           */
    /*------------------------------------------------------------------------*/

    public const int VIN_TEXT = 20;                      // an entry's value: text, one line
    public const int VIN_KEYONLY = 21;                   // a key on a line of its own, no "=" and no value
    public const int VIN_SECTION = 22;                   // a [section] and the lines under it

    /*------------------------------------------------------------------------*/
    /* Defaults for generated text.                                           */
    /*------------------------------------------------------------------------*/

    public const string VIN_DEFAULTDELIMITER = "=";      // between key and value: Windows writes key=value
    public const string VIN_DEFAULTCOMMENTPREFIX = ";";  // the Windows comment marker
    const string VIN_NEWKEYBASE = "newKey";              // a new entry's key, before 1, 2, 3 ... make it free
    const string VIN_NEWSECTIONBASE = "newSection";      // a new section's name, likewise

    /*------------------------------------------------------------------------*/
    /* Vin_ParseLine:                                                         */
    /*                                                                        */
    /* One line as an INI item (see the file header for the rules).           */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sContent : the line from its first non-space character to its line */
    /*                break (not empty).                                      */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesItem : the item; its lead is the line engine's to set.     */
    /*------------------------------------------------------------------------*/
    public static VoorheesItem Vin_ParseLine(string sContent)
    {
        VoorheesItem item;                               // the item made
        VoorheesNode section;                            // a section's container
        string sAfter;                                   // text after a section's "]"
        string sKeyPart;                                 // an entry's text before "="
        string sValuePart;                               // its text after "="
        string sKey;                                     // the key, trimmed
        string sValue;                                   // the value, trimmed
        int iClose;                                      // position of a section's "]"
        int iEquals;                                     // position of an entry's "="
        int iGapEnd;                                     // end of the spaces after "="

        if (sContent[0] == ';' || sContent[0] == '#')
        {   /* A comment line, kept whole. */
            return(VoorheesItem.Vi_NewComment(sContent));
        }

        iClose = sContent.IndexOf(']');
        if (sContent[0] == '[' && iClose > 0)
        {   /* A section: the name up to the first "]". */
            section = VoorheesNode.Vn_NewContainer(VIN_SECTION);
            section.Vn_sClose = "";
            item = VoorheesItem.Vi_NewMember(sContent.Substring(1, iClose - 1),
                section);

            sAfter = sContent.Substring(iClose + 1);
            if (Vin_TrimSpaces(sAfter).Length == 0)
            {   /* Only spaces after it (or nothing). */
                if (sAfter.Length > 0)
                {   /* Trailing spaces: kept as they are. */
                    item.Vi_sValueGap = sAfter;
                }
            }
            else
            {   /* Text after it: that line's comment, its spaces before it. */
                item.Vi_sOpenGap = Vin_LeadingSpaces(sAfter);
                item.Vi_sOpenComment = sAfter.Substring(item.Vi_sOpenGap.Length);
            }

            /* The section, empty until its lines are read. */
            return(item);
        }

        iEquals = sContent.IndexOf('=');
        if (iEquals < 0)
        {   /* No "=": a key on its own, trailing spaces kept apart. */
            sKey = Vin_TrimSpaces(sContent);
            item = VoorheesItem.Vi_NewMember(sKey,
                VoorheesNode.Vn_NewLeaf(VIN_KEYONLY, ""));
            if (sKey.Length < sContent.Length)
            {   /* Trailing spaces. */
                item.Vi_sValueGap = sContent.Substring(sKey.Length);
            }
            return(item);
        }

        /* An entry: key, the gap with "=", the value, trailing spaces. */
        sKeyPart = sContent.Substring(0, iEquals);
        sValuePart = sContent.Substring(iEquals + 1);
        sKey = Vin_TrimSpaces(sKeyPart);
        iGapEnd = Vin_LeadingSpaces(sValuePart).Length;
        sValue = Vin_TrimSpaces(sValuePart.Substring(iGapEnd));

        item = VoorheesItem.Vi_NewMember(sKey,
            VoorheesNode.Vn_NewLeaf(VIN_TEXT, sValue));
        item.Vi_sKeyGap = sKeyPart.Substring(sKey.Length) + "="
            + sValuePart.Substring(0, iGapEnd);
        if (iGapEnd + sValue.Length < sValuePart.Length)
        {   /* Trailing spaces after the value. */
            item.Vi_sValueGap = sValuePart.Substring(iGapEnd + sValue.Length);
        }

        /* The entry as written. */
        return(item);
    }

    /*------------------------------------------------------------------------*/
    /* Vin_LeadingSpaces:                                                     */
    /*                                                                        */
    /* The spaces and tabs a text starts with.                                */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     s : the text.                                                      */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the leading run (possibly "").                            */
    /*------------------------------------------------------------------------*/
    static string Vin_LeadingSpaces(string s)
    {
        int i;

        i = 0;
        while (i < s.Length && (s[i] == ' ' || s[i] == '\t'))
        {
            i++;
        }

        /* Up to the first other character. */
        return(s.Substring(0, i));
    }

    /*------------------------------------------------------------------------*/
    /* Vin_TrimSpaces:                                                        */
    /*                                                                        */
    /* A text without the spaces and tabs at either end (only those: Windows  */
    /* trims nothing else).                                                   */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     s : the text.                                                      */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the trimmed text.                                         */
    /*------------------------------------------------------------------------*/
    static string Vin_TrimSpaces(string s)
    {
        /* Spaces and tabs only. */
        return(s.Trim(new char[] { ' ', '\t' }));
    }

    /*------------------------------------------------------------------------*/
    /* Vin_WriteEntry:                                                        */
    /*                                                                        */
    /* An entry's line: the key, then -- unless it is a key on its own -- the */
    /* gap with its "=" (generated: the document's delimiter) and the value;  */
    /* then any trailing spaces.                                              */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc  : the document (its delimiter style).                         */
    /*     item : the entry.                                                  */
    /*     sb   : where the line goes.                                        */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the line is appended (no line break: leads hold those).     */
    /*------------------------------------------------------------------------*/
    public static void Vin_WriteEntry(VoorheesDocument doc, VoorheesItem item,
        StringBuilder sb)
    {
        sb.Append(item.Vi_sKey);

        if (item.Vi_node.Vn_iKind != VIN_KEYONLY)
        {   /* A value: delimiter, then the value as written. */
            if (item.Vi_sKeyGap != null)
            {   /* As in the file. */
                sb.Append(item.Vi_sKeyGap);
            }
            else
            {   /* New: the file's usual delimiter. */
                sb.Append(doc.Vd_sDelimiter);
            }
            sb.Append(item.Vi_node.Vn_sText);
        }

        if (item.Vi_sValueGap != null)
        {   /* Trailing spaces as written. */
            sb.Append(item.Vi_sValueGap);
        }
    }

    /*------------------------------------------------------------------------*/
    /* Vin_WriteHeader:                                                       */
    /*                                                                        */
    /* A section's header line: [name], then its comment (with the spaces     */
    /* before it, one space when generated) or its trailing spaces.           */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     item : the section.                                                */
    /*     sb   : where the line goes.                                        */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the line is appended.                                       */
    /*------------------------------------------------------------------------*/
    public static void Vin_WriteHeader(VoorheesItem item, StringBuilder sb)
    {
        sb.Append('[');
        sb.Append(item.Vi_sKey);
        sb.Append(']');

        if (item.Vi_sOpenComment != null)
        {   /* A comment on the header line. */
            if (item.Vi_sOpenGap != null)
            {   /* Its spacing as written. */
                sb.Append(item.Vi_sOpenGap);
            }
            else
            {   /* New: one space. */
                sb.Append(' ');
            }
            sb.Append(item.Vi_sOpenComment);
        }
        else if (item.Vi_sValueGap != null)
        {   /* Trailing spaces as written. */
            sb.Append(item.Vi_sValueGap);
        }
    }

    /*------------------------------------------------------------------------*/
    /* Vin_LoadDocument:                                                      */
    /*                                                                        */
    /* A whole file's bytes to an INI document: decoded (any encoding         */
    /* Vtx_Decode knows), read by the line engine, its style detected: line   */
    /* ending, the usual delimiter and the usual comment marker.              */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     bytes  : the file's contents.                                      */
    /*     sPath  : the file, for the document to remember; may be null.      */
    /*     sError : set to null (an INI file always reads).                   */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesDocument : the document.                                   */
    /*------------------------------------------------------------------------*/
    public static VoorheesDocument Vin_LoadDocument(byte[] bytes, string sPath,
        out string sError)
    {
        Encoding encoding;                               // the file's encoding
        bool bBom;                                       // it had a byte order mark
        string sText;                                    // the decoded file
        VoorheesNode root;                               // the document node
        VoorheesDocument doc;                            // the document

        sText = VoorheesText.Vtx_Decode(bytes, out encoding, out bBom);
        root = VoorheesLines.Vln_Parse(VoorheesFormat.VF_INI, sText, null,
            out sError);

        doc = VoorheesDocument.Vd_Create(VoorheesFormat.VF_INI, root, sPath,
            encoding, bBom, VoorheesText.Vtx_DetectNewline(sText), null, null,
            null);
        doc.Vd_sDelimiter = VoorheesLines.Vln_DetectDelimiter(root,
            VIN_DEFAULTDELIMITER);
        doc.Vd_sCommentPrefix = VoorheesLines.Vln_DetectCommentPrefix(root,
            VIN_DEFAULTCOMMENTPREFIX);

        /* The document with its encoding and style. */
        return(doc);
    }

    /*------------------------------------------------------------------------*/
    /* Vin_SaveBytes:                                                         */
    /*                                                                        */
    /* An INI document to the bytes of its file, in its own encoding.         */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc : the document.                                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     byte[] : the file's new contents.                                  */
    /*------------------------------------------------------------------------*/
    public static byte[] Vin_SaveBytes(VoorheesDocument doc)
    {
        /* Text by the line engine, bytes as the file had them. */
        return(VoorheesText.Vtx_Encode(VoorheesLines.Vln_Write(doc),
            doc.Vd_encoding, doc.Vd_bBom));
    }

    /*------------------------------------------------------------------------*/
    /* Vin_NewDocument:                                                       */
    /*                                                                        */
    /* A new, empty INI document: UTF-8 without a BOM, CRLF, "key=value",     */
    /* ";" comments.                                                          */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesDocument : the document.                                   */
    /*------------------------------------------------------------------------*/
    public static VoorheesDocument Vin_NewDocument()
    {
        VoorheesNode root;                               // the empty document node

        root = VoorheesNode.Vn_NewContainer(VoorheesNode.VN_DOCUMENT);

        /* Every style detail the format's default (Vf_ApplyDefaultStyle). */
        return(VoorheesDocument.Vd_Create(VoorheesFormat.VF_INI, root, null,
            null, false, null, null, null, null));
    }

    /*------------------------------------------------------------------------*/
    /* Vin_KindName:                                                          */
    /*                                                                        */
    /* An INI kind's name, for the Type box, menus and messages.              */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iKind : a VIN_ kind.                                               */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the name; "Value" for anything else.                      */
    /*------------------------------------------------------------------------*/
    public static string Vin_KindName(int iKind)
    {
        switch (iKind)
        {
            case VIN_TEXT:    return("Text");                    // key=value
            case VIN_KEYONLY: return("Key Only");                // key alone
            case VIN_SECTION: return("Section");                 // [name]
            default:          return("Value");                   // not an INI kind
        }
    }

    /*------------------------------------------------------------------------*/
    /* Vin_ChildKinds:                                                        */
    /*                                                                        */
    /* What Add Child offers in a container: entries (with a value or a key   */
    /* alone) anywhere, and sections only at the document level.              */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     container : the document node or a section.                        */
    /*                                                                        */
    /* Returns:                                                               */
    /*     List<int> : the kinds, in menu order (a new list).                 */
    /*------------------------------------------------------------------------*/
    public static List<int> Vin_ChildKinds(VoorheesNode container)
    {
        List<int> lstKinds;                              // the kinds

        lstKinds = new List<int>();
        lstKinds.Add(VIN_TEXT);
        lstKinds.Add(VIN_KEYONLY);
        if (container.Vn_iKind == VoorheesNode.VN_DOCUMENT)
        {   /* The top level: sections too. */
            lstKinds.Add(VIN_SECTION);
        }

        /* Entries first, then (at the top) sections. */
        return(lstKinds);
    }

    /*------------------------------------------------------------------------*/
    /* Vin_PanelKinds:                                                        */
    /*                                                                        */
    /* What the Type box offers for an entry: a section stays a section (its  */
    /* lines would have nowhere to go); an entry can switch between having a  */
    /* value and being a key alone.                                           */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     node : the entry's value.                                          */
    /*                                                                        */
    /* Returns:                                                               */
    /*     List<int> : the kinds (a new list).                                */
    /*------------------------------------------------------------------------*/
    public static List<int> Vin_PanelKinds(VoorheesNode node)
    {
        List<int> lstKinds;                              // the kinds

        lstKinds = new List<int>();
        if (node.Vn_iKind == VIN_SECTION)
        {   /* A section: only that. */
            lstKinds.Add(VIN_SECTION);
            return(lstKinds);
        }

        lstKinds.Add(VIN_TEXT);
        lstKinds.Add(VIN_KEYONLY);

        /* An entry: with a value, or the key alone. */
        return(lstKinds);
    }

    /*------------------------------------------------------------------------*/
    /* Vin_ValueProblem:                                                      */
    /*                                                                        */
    /* Why text cannot be an INI value, if it cannot: a value is one line,    */
    /* and Windows strips spaces at its ends (quoting keeps them).            */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sText : the value.                                                 */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the reason, or null when it is a valid value.             */
    /*------------------------------------------------------------------------*/
    public static string Vin_ValueProblem(string sText)
    {
        if (sText.IndexOf('\r') >= 0 || sText.IndexOf('\n') >= 0)
        {   /* Several lines. */
            return("An INI value is one line.");
        }

        if (Vin_TrimSpaces(sText).Length != sText.Length)
        {   /* Spaces or tabs at an end. */
            return("Windows strips spaces at the start and end of an INI "
                + "value; put the value in quotes to keep them.");
        }

        /* A valid value. */
        return(null);
    }

    /*------------------------------------------------------------------------*/
    /* Vin_ValueFromPanel:                                                    */
    /*                                                                        */
    /* The value the edit panel describes: Text from the Value box (one line, */
    /* no spaces at the ends), Key Only (no value), or the section itself.    */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iKind   : the VIN_ kind chosen.                                    */
    /*     sText   : the Value box's text.                                    */
    /*     oldNode : the value being replaced.                                */
    /*     sError  : set to why the value is refused, else null.              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesNode : the value, or null when refused.                    */
    /*------------------------------------------------------------------------*/
    public static VoorheesNode Vin_ValueFromPanel(int iKind, string sText,
        VoorheesNode oldNode, out string sError)
    {
        sError = null;
        if (iKind == VIN_SECTION)
        {   /* A section stays itself (the Type box offers nothing else for it). */
            if (oldNode.Vn_iKind == VIN_SECTION)
            {   /* Unchanged, lines and all. */
                return(oldNode);
            }
            sError = "An entry can't become a section; add a section at the "
                + "top level instead.";
            return(null);
        }

        if (iKind == VIN_KEYONLY)
        {   /* The key alone: no value. */
            return(VoorheesNode.Vn_NewLeaf(VIN_KEYONLY, ""));
        }

        /* Text: must be a valid one-line value. */
        sError = Vin_ValueProblem(sText);
        if (sError != null)
        {   /* Refused, with the reason. */
            return(null);
        }

        /* The text, as typed. */
        return(VoorheesNode.Vn_NewLeaf(VIN_TEXT, sText));
    }

    /*------------------------------------------------------------------------*/
    /* Vin_NewValue:                                                          */
    /*                                                                        */
    /* An empty value of a kind: "" text, a key alone, or an empty section.   */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iKind : a VIN_ kind.                                               */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesNode : the value.                                          */
    /*------------------------------------------------------------------------*/
    public static VoorheesNode Vin_NewValue(int iKind)
    {
        VoorheesNode section;                            // a new section's container

        if (iKind == VIN_SECTION)
        {   /* An empty section. */
            section = VoorheesNode.Vn_NewContainer(VIN_SECTION);
            section.Vn_sClose = "";
            return(section);
        }

        if (iKind == VIN_KEYONLY)
        {   /* A key alone. */
            return(VoorheesNode.Vn_NewLeaf(VIN_KEYONLY, ""));
        }

        /* Empty text. */
        return(VoorheesNode.Vn_NewLeaf(VIN_TEXT, ""));
    }

    /*------------------------------------------------------------------------*/
    /* Vin_ConvertText:                                                       */
    /*                                                                        */
    /* A value from edited text (in-place editing, Replace): text stays text  */
    /* if it is still a valid value; a key alone that is given text becomes   */
    /* key=text ("" leaves it alone); a section has no text.                  */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     oldNode : the value being replaced.                                */
    /*     sText   : the new text.                                            */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesNode : the new value, or null when refused.                */
    /*------------------------------------------------------------------------*/
    public static VoorheesNode Vin_ConvertText(VoorheesNode oldNode,
        string sText)
    {
        if (oldNode.Vn_iKind == VIN_SECTION)
        {   /* A section has no text. */
            return(null);
        }

        if (oldNode.Vn_iKind == VIN_KEYONLY && sText.Length == 0)
        {   /* Still no value. */
            return(oldNode);
        }

        if (Vin_ValueProblem(sText) != null)
        {   /* Not a valid one-line value. */
            return(null);
        }

        /* Text (a key alone gains its "="). */
        return(VoorheesNode.Vn_NewLeaf(VIN_TEXT, sText));
    }

    /*------------------------------------------------------------------------*/
    /* Vin_NewChild:                                                          */
    /*                                                                        */
    /* Add Child's new item: a section named with the first free variant of   */
    /* "newSection", or an entry keyed with the first free variant of         */
    /* "newKey" (with "" text, or a key alone).                               */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc       : the document (for free names).                         */
    /*     container : the document node or a section.                        */
    /*     iKind     : a VIN_ kind.                                           */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesItem : the new item.                                       */
    /*------------------------------------------------------------------------*/
    public static VoorheesItem Vin_NewChild(VoorheesDocument doc,
        VoorheesNode container, int iKind)
    {
        if (iKind == VIN_SECTION)
        {   /* A section with a free name. */
            return(VoorheesItem.Vi_NewMember(doc.Vd_UnusedKey(container,
                VIN_NEWSECTIONBASE), Vin_NewValue(VIN_SECTION)));
        }

        /* An entry with a free key. */
        return(VoorheesItem.Vi_NewMember(doc.Vd_UnusedKey(container,
            VIN_NEWKEYBASE), Vin_NewValue(iKind)));
    }

    /*------------------------------------------------------------------------*/
    /* Vin_NewKeyBase:                                                        */
    /*                                                                        */
    /* The name a new item in a container starts from: "newSection" at the    */
    /* top level (where sections go), else "newKey".                          */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     container : the container.                                         */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the base name.                                            */
    /*------------------------------------------------------------------------*/
    public static string Vin_NewKeyBase(VoorheesNode container)
    {
        if (container.Vn_iKind == VoorheesNode.VN_DOCUMENT)
        {   /* The top level: sections. */
            return(VIN_NEWSECTIONBASE);
        }

        /* Inside a section: keys. */
        return(VIN_NEWKEYBASE);
    }

    /*------------------------------------------------------------------------*/
    /* Vin_FitItem:                                                           */
    /*                                                                        */
    /* Whether an item can go into an INI container: sections only at the     */
    /* top level; every entry needs a key (an unkeyed value -- from a JSON    */
    /* array, say -- has none).  Comments go anywhere.                        */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     container : where it goes.                                         */
    /*     item      : the item.                                              */
    /*     sError    : set to why it cannot go there, else null.              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesItem : the item, or null when refused.                     */
    /*------------------------------------------------------------------------*/
    public static VoorheesItem Vin_FitItem(VoorheesNode container,
        VoorheesItem item, out string sError)
    {
        sError = null;
        if (item.Vi_iKind == VoorheesItem.VI_ELEMENT)
        {   /* No key. */
            sError = "INI entries need a key.";
            return(null);
        }

        if (item.Vi_IsValue() && item.Vi_node.Vn_iKind == VIN_SECTION
            && container.Vn_iKind != VoorheesNode.VN_DOCUMENT)
        {   /* A section inside a section. */
            sError = "A section can only be at the top level of an INI file.";
            return(null);
        }

        /* It fits. */
        return(item);
    }

    /*------------------------------------------------------------------------*/
    /* Vin_KeyProblem:                                                        */
    /*                                                                        */
    /* Why a new key or section name would not read back as itself:           */
    /*     o a section name: no "]" (the name ends at the first one) and no   */
    /*       line break;                                                      */
    /*     o a key: not empty; no "=" (the first one ends the key), no line   */
    /*       break; not starting with ";", "#" or "[" (the line would read as */
    /*       a comment or a section); no spaces at its ends (Windows strips   */
    /*       them).                                                           */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     item    : the entry or section (its value says which).             */
    /*     sNewKey : the proposed key or name.                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the reason, or null when it is acceptable.                */
    /*------------------------------------------------------------------------*/
    public static string Vin_KeyProblem(VoorheesItem item, string sNewKey)
    {
        if (sNewKey.IndexOf('\r') >= 0 || sNewKey.IndexOf('\n') >= 0)
        {   /* A line break, in a name or a key. */
            return("A key or section name is one line.");
        }

        if (item.Vi_node.Vn_iKind == VIN_SECTION)
        {   /* A section name. */
            if (sNewKey.IndexOf(']') >= 0)
            {   /* Would end the name early. */
                return("A section name can't contain \"]\".");
            }
            return(null);
        }

        if (sNewKey.Length == 0)
        {   /* Empty key. */
            return("The key can't be empty.");
        }

        if (sNewKey.IndexOf('=') >= 0)
        {   /* Would end the key early. */
            return("An INI key can't contain \"=\".");
        }

        if (sNewKey[0] == ';' || sNewKey[0] == '#' || sNewKey[0] == '[')
        {   /* The line would read as a comment or a section. */
            return("An INI key can't start with \";\", \"#\" or \"[\".");
        }

        if (Vin_TrimSpaces(sNewKey).Length != sNewKey.Length)
        {   /* Spaces at an end. */
            return("Windows strips spaces at the start and end of a key.");
        }

        /* Acceptable. */
        return(null);
    }

    /*------------------------------------------------------------------------*/
    /* Vin_CommentText:                                                       */
    /*                                                                        */
    /* A comment's words: without its ";" or "#" and one space after it.  A   */
    /* section line's comment that does not start with a marker is shown as   */
    /* it is.                                                                 */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sRaw : the comment as stored (or null).                            */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the words; "" for null.                                   */
    /*------------------------------------------------------------------------*/
    public static string Vin_CommentText(string sRaw)
    {
        if (sRaw == null)
        {   /* No comment. */
            return("");
        }

        if (sRaw.Length > 0 && (sRaw[0] == ';' || sRaw[0] == '#'))
        {   /* A marker: the words after it (and after one space). */
            if (sRaw.Length > 1 && sRaw[1] == ' ')
            {   /* "; words". */
                return(sRaw.Substring(2));
            }
            return(sRaw.Substring(1));
        }

        /* Text after a section's "]" with no marker: as written. */
        return(sRaw);
    }

    /*------------------------------------------------------------------------*/
    /* Vin_MakeComment:                                                       */
    /*                                                                        */
    /* A new comment from its words: the file's usual marker, a space and the */
    /* words (the marker alone for no words).  A comment is one line.         */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sPrefix : the document's comment marker (Vd_sCommentPrefix).       */
    /*     sText   : the words.                                               */
    /*     sError  : set to why they cannot be a comment, else null.          */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the comment, or null when refused.                        */
    /*------------------------------------------------------------------------*/
    public static string Vin_MakeComment(string sPrefix, string sText,
        out string sError)
    {
        sError = null;
        if (sText.IndexOf('\r') >= 0 || sText.IndexOf('\n') >= 0)
        {   /* Several lines. */
            sError = "An INI comment is one line; add another comment entry "
                + "for more.";
            return(null);
        }

        if (sText.Length == 0)
        {   /* No words: the marker alone. */
            return(sPrefix);
        }

        /* Marker, space, words. */
        return(sPrefix + " " + sText);
    }

    /*------------------------------------------------------------------------*/
    /* Vin_RemakeComment:                                                     */
    /*                                                                        */
    /* An existing comment with new words, in its own style: unchanged words  */
    /* give it back as written; otherwise its own marker (and whether a       */
    /* space followed it) with the new words; a section line's comment that   */
    /* had no marker gets the file's usual one.                               */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sPrefix : the document's comment marker.                           */
    /*     sOldRaw : the comment as stored.                                   */
    /*     sText   : the new words.                                           */
    /*     sError  : set to why they cannot be a comment, else null.          */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the comment, or null when refused.                        */
    /*------------------------------------------------------------------------*/
    public static string Vin_RemakeComment(string sPrefix, string sOldRaw,
        string sText, out string sError)
    {
        if (sText == Vin_CommentText(sOldRaw))
        {   /* Same words: exactly as written. */
            sError = null;
            return(sOldRaw);
        }

        if (sOldRaw.Length > 0 && (sOldRaw[0] == ';' || sOldRaw[0] == '#')
            && !(sOldRaw.Length > 1 && sOldRaw[1] == ' ') && sText.Length > 0)
        {   /* Its marker with no space after it: keep that look. */
            if (sText.IndexOf('\r') >= 0 || sText.IndexOf('\n') >= 0)
            {   /* Several lines. */
                sError = "An INI comment is one line; add another comment "
                    + "entry for more.";
                return(null);
            }
            sError = null;
            return(sOldRaw.Substring(0, 1) + sText);
        }

        if (sOldRaw.Length > 0 && (sOldRaw[0] == ';' || sOldRaw[0] == '#'))
        {   /* Its own marker, a space, the words. */
            return(Vin_MakeComment(sOldRaw.Substring(0, 1), sText, out sError));
        }

        /* No marker before: the file's usual one. */
        return(Vin_MakeComment(sPrefix, sText, out sError));
    }

    /*------------------------------------------------------------------------*/
    /* Vin_ResetLayout:                                                       */
    /*                                                                        */
    /* Clears a copied item's layout -- lead, key gap, trailing spaces, the   */
    /* space before a header comment, and inside a section the same for its   */
    /* lines -- so it is written in the document's style (Paste).  Keys,      */
    /* values and comments stay.  ONLY for a copy not in a document.          */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     item : the copy.                                                   */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the layout fields are null.                                 */
    /*------------------------------------------------------------------------*/
    public static void Vin_ResetLayout(VoorheesItem item)
    {
        int i;

        item.Vi_sLead = null;
        item.Vi_sKeyGap = null;
        item.Vi_sValueGap = null;
        item.Vi_sOpenGap = null;

        if (item.Vi_node != null && item.Vi_node.Vn_lstItems != null)
        {   /* A section: its lines too. */
            item.Vi_node.Vn_sClose = "";
            for (i = 0; i < item.Vi_node.Vn_lstItems.Count; i++)
            {
                Vin_ResetLayout(item.Vi_node.Vn_lstItems[i]);
            }
        }
    }

    /*------------------------------------------------------------------------*/
    /* Vin_CountKeyOnly:                                                      */
    /*                                                                        */
    /* How many keys stand alone on a line (no "=", no value) in a container  */
    /* and its sections, for --validate's warning.                            */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     container : the document node (or a section).                      */
    /*                                                                        */
    /* Returns:                                                               */
    /*     int : the count.                                                   */
    /*------------------------------------------------------------------------*/
    public static int Vin_CountKeyOnly(VoorheesNode container)
    {
        VoorheesItem item;                               // an item looked at
        int iCount;                                      // keys alone so far
        int i;

        iCount = 0;
        for (i = 0; i < container.Vn_Count(); i++)
        {
            item = container.Vn_lstItems[i];
            if (!item.Vi_IsValue())
            {   /* A comment. */
                continue;
            }

            if (item.Vi_node.Vn_IsContainer())
            {   /* A section: its keys. */
                iCount += Vin_CountKeyOnly(item.Vi_node);
            }
            else if (item.Vi_node.Vn_iKind == VIN_KEYONLY)
            {   /* A key alone. */
                iCount++;
            }
        }

        /* Every one found. */
        return(iCount);
    }

    /*------------------------------------------------------------------------*/
    /* Vin_OpenWarnings:                                                      */
    /*                                                                        */
    /* What the user should be told when an INI file opens: repeated keys or  */
    /* sections, which Windows resolves by reading the FIRST copy, while      */
    /* Voorhees keeps them all.                                               */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sFileName    : the file's name (no folder).                        */
    /*     iExtraCopies : repeated-name copies (Vd_ExtraCopies, compared      */
    /*                    without case).                                      */
    /*                                                                        */
    /* Returns:                                                               */
    /*     List<string> : the messages (a new list; empty for none).          */
    /*------------------------------------------------------------------------*/
    public static List<string> Vin_OpenWarnings(string sFileName,
        int iExtraCopies)
    {
        List<string> lstWarnings;                        // the messages

        lstWarnings = new List<string>();
        if (iExtraCopies > 0)
        {   /* Something repeats: say what Windows and Voorhees do with it. */
            lstWarnings.Add(sFileName + " repeats a section or a key ("
                + iExtraCopies.ToString(CultureInfo.InvariantCulture)
                + " extra cop" + (iExtraCopies == 1 ? "y" : "ies")
                + "; names are compared without case).\r\n\r\n"
                + "Windows reads the FIRST copy of a repeated key.  Voorhees "
                + "keeps every copy, marks them \"(duplicate)\" in the tree "
                + "and saves them all back.  Delete or rename the copies you "
                + "do not want.");
        }

        /* Every warning, or none. */
        return(lstWarnings);
    }
}
