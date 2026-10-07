/*----------------------------------------------------------------------------*/
/* VoorheesJson.cs                                                            */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/*----------------------------------------------------------------------------*/
/* Voorhees' own JSON reader and writer: the only code that turns JSON text   */
/* into the model of VoorheesDocument.cs and back (the bytes themselves are   */
/* VoorheesText.cs's work).  No JSON library is used.                         */
/*                                                                            */
/* LOSSLESS.  The reader keeps every character of the file in the model --    */
/* tokens exactly as written, and all whitespace and comments as trivia on    */
/* the items -- and the writer emits every stored string exactly as it is,    */
/* so writing an unedited document gives back the input byte for byte.        */
/* Only fields left null (new or edited items) are generated, in the          */
/* document's style.                                                          */
/*                                                                            */
/* STRICT.  Standard JSON (RFC 8259) plus // and block comments wherever      */
/* whitespace may appear, and nothing else: no trailing commas, no            */
/* mismatched brackets, no bad escapes, no raw control characters in          */
/* strings, no leading zeros or bare decimal points in numbers, no            */
/* misspelt literals, one value per file.  Every error names its line and     */
/* column.                                                                    */
/*                                                                            */
/* WHERE COMMENTS GO.  Within a container (and the document):                 */
/*     1. after an opening bracket or a comma, comments on the SAME line      */
/*        belong to the line before: after a comma, to the previous item as   */
/*        its end-of-line comment; after a bracket, to the item that holds    */
/*        the container (Vi_sOpenComment);                                    */
/*     2. a comment that starts on a NEW line is an item of its own (with     */
/*        any further comments that start on the same line);                  */
/*     3. the whitespace before an item is its lead;                          */
/*     4. after the last value, a same-line comment is its end-of-line        */
/*        comment, later own-line comments are comment items, and the         */
/*        remaining whitespace is the container's closing text;               */
/*     5. comments anywhere else -- between a key and its colon, a colon and  */
/*        its value, a value and its comma -- stay inside that gap and mark   */
/*        the item as having odd comments (editable only through Raw).        */
/*                                                                            */
/* Classes:                                                                   */
/*     VoorheesJson       (Vjs_) : the public entry points: load and save a   */
/*                                 document, parse, write, render one item,   */
/*                                 parse a Raw edit, comment helpers, style   */
/*                                 detection.                                 */
/*     VoorheesJsonParser (Vjp_) : one parse, over one string.                */
/*     VoorheesJsonWriter (Vjw_) : one write, into one StringBuilder.         */
/*----------------------------------------------------------------------------*/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

/*----------------------------------------------------------------------------*/
/* VoorheesJson                                                               */
/*                                                                            */
/* Static functions only; nothing is kept between calls, so any of them may   */
/* run on a worker thread.                                                    */
/*----------------------------------------------------------------------------*/
static class VoorheesJson
{
    /*------------------------------------------------------------------------*/
    /* Limits and defaults.                                                   */
    /*------------------------------------------------------------------------*/

    public const int VJS_MAXDEPTH = 1000;                // deepest nesting read: the reader and writer recurse, and this keeps them well inside the stack
    public const int VJS_MAXINDENTSPACES = 8;            // a first indent wider than this is not taken as one level
    public const string VJS_DEFAULTCOMMENTGAP = "  ";    // generated space before an end-of-line or open comment

    /*------------------------------------------------------------------------*/
    /* JSON's kinds of node (VoorheesNode.Vn_iKind).  Every format module     */
    /* numbers its kinds in a range of its own (JSON 10-19, INI 20-29, CFG    */
    /* 30-39, REG 40-49), so a kind alone says                                */
    /* which format it belongs to; the core keeps only VN_DOCUMENT (1).       */
    /* What each holds:                                                       */
    /*     VJS_STRING  : Vn_sText is the string itself (unescaped); Vn_sRaw   */
    /*                   is the token as written, quotes and escapes          */
    /*                   included, or null when generating it from Vn_sText   */
    /*                   gives exactly that (no escapes were used).           */
    /*     VJS_NUMBER  : Vn_sText is the number as written, always valid      */
    /*                   JSON number syntax; Vn_sRaw is null (the text IS     */
    /*                   the token).                                          */
    /*     VJS_BOOLEAN : Vn_sText "true" or "false"; Vn_sRaw null.            */
    /*     VJS_NULL    : Vn_sText "null"; Vn_sRaw null.                       */
    /*     VJS_OBJECT  : Vn_lstItems, members and comments in file order (a   */
    /*                   key may appear more than once); Vn_sClose;           */
    /*                   Vn_bOneLine.                                         */
    /*     VJS_ARRAY   : Vn_lstItems, elements and comments in order;         */
    /*                   Vn_sClose; Vn_bOneLine.                              */
    /* A JSON document node (VN_DOCUMENT) holds top-level comments and        */
    /* exactly one element, the top value; its Vn_sClose is the text after    */
    /* the last item (the final line break lives there).                      */
    /*------------------------------------------------------------------------*/

    public const int VJS_STRING = 10;                    // text
    public const int VJS_NUMBER = 11;                    // number, kept exactly as written
    public const int VJS_BOOLEAN = 12;                   // true or false
    public const int VJS_NULL = 13;                      // null
    public const int VJS_OBJECT = 14;                    // keyed members, in file order
    public const int VJS_ARRAY = 15;                     // elements, in order

    /*------------------------------------------------------------------------*/
    /* The value kinds in the order the Type box and the Add Child menu list  */
    /* them: plain values first, then the two containers.                     */
    /*------------------------------------------------------------------------*/

    static readonly int[] VJS_VALUEKINDS = new int[]
    {
        VJS_STRING, VJS_NUMBER, VJS_BOOLEAN, VJS_NULL,   // plain values
        VJS_OBJECT, VJS_ARRAY                            // containers
    };

    /*------------------------------------------------------------------------*/
    /* Strict JSON number syntax: optional minus, integer part without a      */
    /* leading zero, optional fraction, optional exponent.                    */
    /*------------------------------------------------------------------------*/

    static readonly Regex VJS_NUMBERPATTERN = new Regex(
        @"^-?(0|[1-9][0-9]*)(\.[0-9]+)?([eE][+-]?[0-9]+)?$");    // matches text that is already a valid JSON number

    /*------------------------------------------------------------------------*/
    /* Vjs_KindName:                                                          */
    /*                                                                        */
    /* A JSON kind's name, as the Type box, the Add Child menu and messages   */
    /* show it.                                                               */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iKind : a VJS_ kind.                                               */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the name; "Value" for anything that is not a JSON kind.   */
    /*------------------------------------------------------------------------*/
    public static string Vjs_KindName(int iKind)
    {
        switch (iKind)
        {
            case VJS_STRING:  return("String");                  // text
            case VJS_NUMBER:  return("Number");                  // number
            case VJS_BOOLEAN: return("Boolean");                 // true / false
            case VJS_NULL:    return("Null");                    // null
            case VJS_OBJECT:  return("Object");                  // { }
            case VJS_ARRAY:   return("Array");                   // [ ]
            default:          return("Value");                   // not a JSON kind
        }
    }

    /*------------------------------------------------------------------------*/
    /* Vjs_ChildKinds:                                                        */
    /*                                                                        */
    /* The kinds Add Child offers in a container, in menu order: every value  */
    /* kind in an object or array; none in the document node, which holds     */
    /* exactly one value (it takes comments only, offered separately).        */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     container : the container the child would go into.                 */
    /*                                                                        */
    /* Returns:                                                               */
    /*     List<int> : the kinds (a new list; may be empty).                  */
    /*------------------------------------------------------------------------*/
    public static List<int> Vjs_ChildKinds(VoorheesNode container)
    {
        if (container.Vn_iKind == VoorheesNode.VN_DOCUMENT)
        {   /* The document: its one value already exists. */
            return(new List<int>());
        }

        /* An object or array: anything. */
        return(new List<int>(VJS_VALUEKINDS));
    }

    /*------------------------------------------------------------------------*/
    /* Vjs_PanelKinds:                                                        */
    /*                                                                        */
    /* The kinds the Type box offers for a value: in JSON any value, the top  */
    /* value included, may become any kind.                                   */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     List<int> : the kinds in Type box order (a new list).              */
    /*------------------------------------------------------------------------*/
    public static List<int> Vjs_PanelKinds()
    {
        /* Every value kind, plain values first. */
        return(new List<int>(VJS_VALUEKINDS));
    }

    /*------------------------------------------------------------------------*/
    /* Vjs_KindHasText:                                                       */
    /*                                                                        */
    /* Whether a kind's value is typed (the Value box is enabled): strings,   */
    /* numbers and booleans (whose checkbox starts from the box's text);      */
    /* null and the containers have nothing to type.                          */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iKind : a VJS_ kind.                                               */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true when the value has text.                               */
    /*------------------------------------------------------------------------*/
    public static bool Vjs_KindHasText(int iKind)
    {
        /* Everything but null, object and array. */
        return(iKind != VJS_NULL && iKind != VJS_OBJECT && iKind != VJS_ARRAY);
    }

    /*------------------------------------------------------------------------*/
    /* Vjs_ValueFromPanel:                                                    */
    /*                                                                        */
    /* The value the edit panel describes, for its Save:                      */
    /*     o Number  : the Value box read with "." as the decimal point       */
    /*                 whatever the locale (Vjs_MakeNumber); refused if it is */
    /*                 not a number;                                          */
    /*     o Boolean : the checkbox;                                          */
    /*     o Null    : null;                                                  */
    /*     o Object / Array : the old value itself if it is already of that   */
    /*                 kind (unchanged, items and all), else a new empty one; */
    /*     o String  : the Value box.  The box shows line breaks as CRLF;     */
    /*                 they are stored as plain LF unless the old value was   */
    /*                 itself a string using CRLF, so an unchanged value      */
    /*                 stays the same.                                        */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iKind    : the VJS_ kind chosen in the Type box.                   */
    /*     sText    : the Value box's text (CRLF line breaks).                */
    /*     bChecked : the Boolean checkbox.                                   */
    /*     oldNode  : the value being replaced.                               */
    /*     sError   : set to why the panel's value is refused, else null.     */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesNode : the value, or null when refused.                    */
    /*------------------------------------------------------------------------*/
    public static VoorheesNode Vjs_ValueFromPanel(int iKind, string sText,
        bool bChecked, VoorheesNode oldNode, out string sError)
    {
        VoorheesNode node;                               // the value built
        string sValue;                                   // string text, line breaks adjusted

        sError = null;
        switch (iKind)
        {
            case VJS_NUMBER:
                /* Must read as a number. */
                node = Vjs_MakeNumber(sText);
                if (node == null)
                {   /* Not a number: say why. */
                    sError = "\"" + sText.Trim() + "\" is not a valid number.";
                }
                break;

            case VJS_BOOLEAN:
                /* The checkbox stands in for the Value box. */
                if (bChecked)
                {   /* Ticked. */
                    node = VoorheesNode.Vn_NewLeaf(VJS_BOOLEAN, "true");
                }
                else
                {   /* Cleared. */
                    node = VoorheesNode.Vn_NewLeaf(VJS_BOOLEAN, "false");
                }
                break;

            case VJS_NULL:
                /* Nothing to read. */
                node = VoorheesNode.Vn_NewLeaf(VJS_NULL, "null");
                break;

            case VJS_OBJECT:
            case VJS_ARRAY:
                if (oldNode.Vn_iKind == iKind)
                {   /* Already that kind: keep it and its items. */
                    node = oldNode;
                }
                else
                {   /* Becoming a container: start empty. */
                    node = Vjs_NewDefault(iKind);
                }
                break;

            default:
                /* String: CRLF from the box becomes LF unless the original was a CRLF string. */
                sValue = sText;
                if (!(oldNode.Vn_iKind == VJS_STRING
                    && oldNode.Vn_sText.IndexOf("\r\n", StringComparison.Ordinal) >= 0))
                {   /* Original was not a CRLF string: CRLF from the box becomes LF. */
                    sValue = sValue.Replace("\r\n", "\n");
                }
                node = VoorheesNode.Vn_NewLeaf(VJS_STRING, sValue);
                break;
        }

        /* The value, or null with the reason. */
        return(node);
    }

    /*------------------------------------------------------------------------*/
    /* Vjs_NewDefault:                                                        */
    /*                                                                        */
    /* An empty JSON value of a kind, for Add Child: "", 0, false, null, {}   */
    /* or [].                                                                 */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iKind : a VJS_ kind.                                               */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesNode : the new value; an empty string for an unknown kind. */
    /*------------------------------------------------------------------------*/
    public static VoorheesNode Vjs_NewDefault(int iKind)
    {
        switch (iKind)
        {
            case VoorheesJson.VJS_NUMBER:  return(VoorheesNode.Vn_NewLeaf(VoorheesJson.VJS_NUMBER, "0"));       // 0
            case VoorheesJson.VJS_BOOLEAN: return(VoorheesNode.Vn_NewLeaf(VoorheesJson.VJS_BOOLEAN, "false"));  // false
            case VoorheesJson.VJS_NULL:    return(VoorheesNode.Vn_NewLeaf(VoorheesJson.VJS_NULL, "null"));      // null
            case VoorheesJson.VJS_OBJECT:  return(VoorheesNode.Vn_NewContainer(VoorheesJson.VJS_OBJECT));       // {}
            case VoorheesJson.VJS_ARRAY:   return(VoorheesNode.Vn_NewContainer(VoorheesJson.VJS_ARRAY));        // []
            default:                      return(VoorheesNode.Vn_NewLeaf(VoorheesJson.VJS_STRING, ""));         // "" (String, or anything unknown)
        }
    }

    /*------------------------------------------------------------------------*/
    /* Vjs_ItemName:                                                          */
    /*                                                                        */
    /* The name a value entry is labelled with in the tree: a member's key,   */
    /* an array element's index, or "root" for the top value.                 */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     container     : the entry's container.                             */
    /*     iPos          : its position.                                      */
    /*     iElementIndex : an element's index if the caller is counting them  */
    /*                     as it goes; -1 to have it counted here.            */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the name (as stored: the tree makes it one line).         */
    /*------------------------------------------------------------------------*/
    public static string Vjs_ItemName(VoorheesNode container, int iPos,
        int iElementIndex)
    {
        VoorheesItem item;                               // the entry

        item = container.Vn_lstItems[iPos];
        if (item.Vi_iKind == VoorheesItem.VI_MEMBER)
        {   /* An object member: its key. */
            return(item.Vi_sKey);
        }

        if (container.Vn_iKind == VoorheesNode.VN_DOCUMENT)
        {   /* The top value. */
            return("root");
        }

        if (iElementIndex < 0)
        {   /* One label on its own: count the values before it. */
            iElementIndex = container.Vn_ElementIndex(iPos);
        }

        /* An array element: its index. */
        return(iElementIndex.ToString(CultureInfo.InvariantCulture));
    }

    /*------------------------------------------------------------------------*/
    /* Vjs_LabelBrackets:                                                     */
    /*                                                                        */
    /* The brackets a container's tree label puts round its name: {name} for  */
    /* an object, [name] for an array.                                        */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iKind  : a VJS_ kind.                                              */
    /*     sOpen  : set to the opening bracket, or null.                      */
    /*     sClose : set to the closing bracket, or null.                      */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true for a container (brackets set); false for a plain      */
    /*            value, labelled "name: value".                              */
    /*------------------------------------------------------------------------*/
    public static bool Vjs_LabelBrackets(int iKind, out string sOpen,
        out string sClose)
    {
        sOpen = null;
        sClose = null;
        if (iKind == VJS_OBJECT)
        {   /* Object: {name}. */
            sOpen = "{";
            sClose = "}";
            return(true);
        }

        if (iKind == VJS_ARRAY)
        {   /* Array: [name]. */
            sOpen = "[";
            sClose = "]";
            return(true);
        }

        /* A plain value: no brackets. */
        return(false);
    }

    /*------------------------------------------------------------------------*/
    /* Vjs_LabelLineComment:                                                  */
    /*                                                                        */
    /* An entry's line comment as its tree label shows it after the value, in */
    /* grey: "// " and its words (whatever its own markers were).             */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sRaw : the comment as stored.                                      */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the text to show (the tree makes it one line).            */
    /*------------------------------------------------------------------------*/
    public static string Vjs_LabelLineComment(string sRaw)
    {
        /* Always shown as a line comment, words only. */
        return("// " + Vjs_CommentText(sRaw));
    }

    /*-------------------------------------------------------------------------*/
    /* Vjs_OpenWarnings:                                                       */
    /*                                                                         */
    /* What the user should be told when a JSON file opens: repeated keys are  */
    /* legal JSON but most programs keep only the LAST copy of each, while     */
    /* Voorhees keeps them all -- worth knowing before editing or saving.      */
    /*                                                                         */
    /* Arguments:                                                              */
    /*     sFileName    : the file's name (no folder), for the message.        */
    /*     iExtraCopies : repeated-key copies in it (Vd_ExtraCopies).          */
    /*                                                                         */
    /* Returns:                                                                */
    /*     List<string> : the messages, in order (a new list; empty for none). */
    /*-------------------------------------------------------------------------*/
    public static List<string> Vjs_OpenWarnings(string sFileName,
        int iExtraCopies)
    {
        List<string> lstWarnings;                        // the messages

        lstWarnings = new List<string>();
        if (iExtraCopies > 0)
        {   /* The file repeats at least one key within an object: explain what Voorhees does with it. */
            lstWarnings.Add(sFileName + " repeats a key "
                + "within an object (" + iExtraCopies.ToString()
                + " extra cop" + (iExtraCopies == 1 ? "y" : "ies")
                + ").\r\n\r\n"
                + "JSON allows this, but many programs keep only the "
                + "LAST copy of a repeated key.  Voorhees keeps every "
                + "copy, marks them \"(duplicate)\" in the tree and saves "
                + "them all back.  Delete or rename the copies you do not "
                + "want.");
        }

        /* Every warning, or none. */
        return(lstWarnings);
    }

    /*------------------------------------------------------------------------*/
    /* Vjs_EditWarning:                                                       */
    /*                                                                        */
    /* JSON's one warning about an edit: the first comment in a document that */
    /* had none makes it no longer standard JSON (it is JSONC: Voorhees reads */
    /* it, many programs refuse it; undo takes the comment away again).  The  */
    /* editor shows it once per document.                                     */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     bHadComments : the document held a comment before the change.      */
    /*     bHasComments : it holds one now.                                   */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the message, or null when there is nothing to say.        */
    /*------------------------------------------------------------------------*/
    public static string Vjs_EditWarning(bool bHadComments, bool bHasComments)
    {
        if (!bHasComments || bHadComments)
        {   /* No comment now, or there were some already: nothing new. */
            return(null);
        }

        /* The first comment in a standard JSON document. */
        return("This document now contains a comment, so it is no "
            + "longer standard JSON (it is \"JSONC\", JSON with "
            + "comments).\r\n\r\n"
            + "Voorhees reads and writes it, and so do many tools, but "
            + "programs that expect standard JSON will refuse the file.  "
            + "Undo removes the comment again.");
    }

    /*------------------------------------------------------------------------*/
    /* Vjs_NewDocument:                                                       */
    /*                                                                        */
    /* A new, never-saved JSON document holding an empty object, in the       */
    /* default style (UTF-8 without a BOM, CRLF, two-space indentation,       */
    /* "key": value): File > New.                                             */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesDocument : the new document.                               */
    /*------------------------------------------------------------------------*/
    public static VoorheesDocument Vjs_NewDocument()
    {
        VoorheesNode root;                               // the document node

        /* One top value, {}, and every piece of layout to be generated   */
        /* (the writer adds the final line break).                        */
        root = VoorheesNode.Vn_NewContainer(VoorheesNode.VN_DOCUMENT);
        root.Vn_lstItems.Add(VoorheesItem.Vi_NewElement(
            VoorheesNode.Vn_NewContainer(VJS_OBJECT)));

        /* Every style detail left to the defaults. */
        return(VoorheesDocument.Vd_Create(VoorheesFormat.VF_JSON, root, null,
            null, false, null, null, null, null));
    }

    /*------------------------------------------------------------------------*/
    /* Vjs_InitialPath:                                                       */
    /*                                                                        */
    /* What the tree selects when a JSON document is shown: the top value     */
    /* (the document's one value item, among any top-level comments), opened  */
    /* one level.                                                             */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc     : the document.                                            */
    /*     bExpand : set to true: the top value is opened.                    */
    /*                                                                        */
    /* Returns:                                                               */
    /*     List<int> : the top value's one-entry path, or null when there is  */
    /*                 no value (never, for a document that was read).        */
    /*------------------------------------------------------------------------*/
    public static List<int> Vjs_InitialPath(VoorheesDocument doc,
        out bool bExpand)
    {
        List<int> lstPath;                               // the path being built
        int iPos;                                        // the top value's position

        bExpand = true;

        /* The document holds exactly one value, so the last is the one. */
        iPos = doc.Vd_root.Vn_LastValuePos();
        if (iPos < 0)
        {   /* No value at all: nothing to select. */
            return(null);
        }

        lstPath = new List<int>();
        lstPath.Add(iPos);

        /* Its position among the top-level items. */
        return(lstPath);
    }

    /*------------------------------------------------------------------------*/
    /* Vjs_NewKeyBase:                                                        */
    /*                                                                        */
    /* The key a new object member starts from ("newKey", then "newKey1" ...  */
    /* whichever is free: VoorheesDocument.Vd_UnusedKey).                     */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : "newKey".                                                 */
    /*------------------------------------------------------------------------*/
    public static string Vjs_NewKeyBase()
    {
        /* camelCase, as JSON keys usually are. */
        return("newKey");
    }

    /*------------------------------------------------------------------------*/
    /* Vjs_NewChild:                                                          */
    /*                                                                        */
    /* Add Child's new item for a kind: in an object a member keyed with the  */
    /* first free variant of "newKey"; in an array an element.  Its value is  */
    /* the kind's empty value (Vjs_NewDefault).                               */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc       : the document (for a free key).                         */
    /*     container : the object or array.                                   */
    /*     iKind     : a VJS_ kind.                                           */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesItem : the new item, not in any document.                  */
    /*------------------------------------------------------------------------*/
    public static VoorheesItem Vjs_NewChild(VoorheesDocument doc,
        VoorheesNode container, int iKind)
    {
        if (container.Vn_iKind == VJS_OBJECT)
        {   /* A member with a free key. */
            return(VoorheesItem.Vi_NewMember(doc.Vd_UnusedKey(container,
                Vjs_NewKeyBase()), Vjs_NewDefault(iKind)));
        }

        /* An element. */
        return(VoorheesItem.Vi_NewElement(Vjs_NewDefault(iKind)));
    }

    /*------------------------------------------------------------------------*/
    /* Vjs_FitItem:                                                           */
    /*                                                                        */
    /* An item made to fit the JSON container it is going into:               */
    /*     o the document node holds exactly one value, so a value is         */
    /*       refused there (comments are fine);                               */
    /*     o a member put into an array becomes an element (its key and the   */
    /*       text around it dropped);                                         */
    /*     o an element put into an object becomes a member keyed with the    */
    /*       first free variant of "newKey".                                  */
    /* Anything else fits as it is.                                           */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc       : the document (for a free key).                         */
    /*     container : where the item goes.                                   */
    /*     item      : the item (not in any document; not changed).           */
    /*     sError    : set to why it cannot go there, else null.              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesItem : the item to insert (item itself, or a converted     */
    /*                    copy), or null when refused.                        */
    /*------------------------------------------------------------------------*/
    public static VoorheesItem Vjs_FitItem(VoorheesDocument doc,
        VoorheesNode container, VoorheesItem item, out string sError)
    {
        VoorheesItem fitted;                             // a converted copy

        sError = null;
        if (container.Vn_iKind == VoorheesNode.VN_DOCUMENT && item.Vi_IsValue())
        {   /* The document holds one value only. */
            sError = "The document holds one value; add entries inside it.";
            return(null);
        }

        if (container.Vn_iKind == VJS_ARRAY
            && item.Vi_iKind == VoorheesItem.VI_MEMBER)
        {   /* A member into an array: it loses its key. */
            fitted = item.Vi_Copy();
            fitted.Vi_iKind = VoorheesItem.VI_ELEMENT;
            fitted.Vi_sKey = null;
            fitted.Vi_sKeyRaw = null;
            fitted.Vi_sKeyGap = null;
            fitted.Vi_sColonGap = null;
            return(fitted);
        }

        if (container.Vn_iKind == VJS_OBJECT
            && item.Vi_iKind == VoorheesItem.VI_ELEMENT)
        {   /* An element into an object: it takes a free key. */
            fitted = item.Vi_Copy();
            fitted.Vi_iKind = VoorheesItem.VI_MEMBER;
            fitted.Vi_sKey = doc.Vd_UnusedKey(container, Vjs_NewKeyBase());
            fitted.Vi_sKeyRaw = null;
            return(fitted);
        }

        /* Fits as it is. */
        return(item);
    }

    /*------------------------------------------------------------------------*/
    /* Vjs_RemoveProblem:                                                     */
    /*                                                                        */
    /* Why an item may not be deleted, if it may not: the top value (a JSON   */
    /* document holds exactly one value; it can be changed, not removed).     */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     container : the item's container.                                  */
    /*     item      : the item.                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the reason, or null when it may be deleted.               */
    /*------------------------------------------------------------------------*/
    public static string Vjs_RemoveProblem(VoorheesNode container,
        VoorheesItem item)
    {
        if (container.Vn_iKind == VoorheesNode.VN_DOCUMENT && item.Vi_IsValue())
        {   /* The top value. */
            return("The top value can't be deleted; change it instead.");
        }

        /* Anything else may go. */
        return(null);
    }

    /*------------------------------------------------------------------------*/
    /* Vjs_KeyProblem:                                                        */
    /*                                                                        */
    /* Why a member may not take a new key, by JSON's syntax: an empty key is */
    /* legal JSON but almost always a slip, so it is refused (the caller has  */
    /* already let an unchanged key through, so a member whose key was empty  */
    /* in the file keeps it).  Any other string is a valid key.               */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sNewKey : the proposed key.                                        */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the reason, or null when the key is acceptable.           */
    /*------------------------------------------------------------------------*/
    public static string Vjs_KeyProblem(string sNewKey)
    {
        if (sNewKey.Length == 0)
        {   /* Empty key. */
            return("The key can't be empty.");
        }

        /* Any other text. */
        return(null);
    }

    /*------------------------------------------------------------------------*/
    /* Vjs_MakeNumber:                                                        */
    /*                                                                        */
    /* A number value from typed text, read with "." as the decimal point     */
    /* whatever the Windows locale.  Text that is already a valid JSON number */
    /* is kept EXACTLY as typed, so a 19-digit ID or a decimal like 1.10      */
    /* survives.  Other text a double accepts (".5", "+3") is stored in       */
    /* round-trip form.  NaN and infinity are refused: JSON cannot express    */
    /* them.                                                                  */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sText : the typed text; surrounding spaces are ignored.            */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesNode : the number, or null if the text is not one.         */
    /*------------------------------------------------------------------------*/
    public static VoorheesNode Vjs_MakeNumber(string sText)
    {
        string sTrimmed;                                 // text without surrounding spaces
        double dblValue;                                 // its numeric value

        sTrimmed = sText.Trim();
        if (VJS_NUMBERPATTERN.IsMatch(sTrimmed))
        {   /* Already valid JSON: keep the exact digits as typed, however   */
            /* large (a double would round them).                            */
            return(VoorheesNode.Vn_NewLeaf(VoorheesJson.VJS_NUMBER, sTrimmed));
        }

        if (!double.TryParse(sTrimmed, NumberStyles.Float,
            CultureInfo.InvariantCulture, out dblValue))
        {   /* Not a number at all. */
            return(null);
        }

        if (double.IsNaN(dblValue) || double.IsInfinity(dblValue))
        {   /* "NaN", "Infinity" or an overflow: not representable in JSON. */
            return(null);
        }

        /* Valid only to .NET: store the value in round-trip form, which is   */
        /* valid JSON ("R" never writes a leading "." or "+").                */
        return(VoorheesNode.Vn_NewLeaf(VoorheesJson.VJS_NUMBER,
            dblValue.ToString("R", CultureInfo.InvariantCulture)));
    }

    /*------------------------------------------------------------------------*/
    /* Vjs_ParseValue:                                                        */
    /*                                                                        */
    /* Guesses a value's kind from text: null, true, false, a number, or      */
    /* otherwise a string.  Used only when the old value was null, which has  */
    /* no kind of its own to keep (see Vjs_ConvertText).                      */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sText : the typed text.                                            */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesNode : the value; never null.                              */
    /*------------------------------------------------------------------------*/
    public static VoorheesNode Vjs_ParseValue(string sText)
    {
        string sLower;                                   // trimmed, lower-case text for the keywords
        VoorheesNode number;                             // the text as a number, if it is one

        sLower = sText.Trim().ToLowerInvariant();

        if (sLower == "null")
        {   /* The null keyword. */
            return(VoorheesNode.Vn_NewLeaf(VoorheesJson.VJS_NULL, "null"));
        }

        if (sLower == "true" || sLower == "false")
        {   /* A boolean keyword. */
            return(VoorheesNode.Vn_NewLeaf(VoorheesJson.VJS_BOOLEAN, sLower));
        }

        number = Vjs_MakeNumber(sText);
        if (number != null)
        {   /* Reads as a number. */
            return(number);
        }

        /* Anything else is a string, kept exactly as typed. */
        return(VoorheesNode.Vn_NewLeaf(VoorheesJson.VJS_STRING, sText));
    }

    /*------------------------------------------------------------------------*/
    /* Vjs_ConvertText:                                                       */
    /*                                                                        */
    /* A new value of the SAME kind as an old one, from edited text: a        */
    /* string stays a string, a number must still be a number, a boolean      */
    /* must be true or false.  A null has no kind to keep, so its kind is     */
    /* guessed from the text.  Used by in-place editing and Replace.          */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     oldNode : the value being replaced (its kind is kept).             */
    /*     sText   : the new text.                                            */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesNode : the new value, or null when the text is not valid   */
    /*                    for the old value's kind (or it is a container).    */
    /*------------------------------------------------------------------------*/
    public static VoorheesNode Vjs_ConvertText(VoorheesNode oldNode, string sText)
    {
        string sLower;                                   // trimmed, lower-case text for booleans

        if (oldNode.Vn_iKind == VoorheesJson.VJS_STRING)
        {   /* String: any text at all, exactly as typed. */
            return(VoorheesNode.Vn_NewLeaf(VoorheesJson.VJS_STRING, sText));
        }

        if (oldNode.Vn_iKind == VoorheesJson.VJS_NUMBER)
        {   /* Number: must still read as one (null otherwise). */
            return(Vjs_MakeNumber(sText));
        }

        if (oldNode.Vn_iKind == VoorheesJson.VJS_BOOLEAN)
        {   /* Boolean: only the two keywords. */
            sLower = sText.Trim().ToLowerInvariant();
            if (sLower == "true" || sLower == "false")
            {   /* One of them. */
                return(VoorheesNode.Vn_NewLeaf(VoorheesJson.VJS_BOOLEAN, sLower));
            }

            /* Neither keyword: refuse. */
            return(null);
        }

        if (oldNode.Vn_iKind == VoorheesJson.VJS_NULL)
        {   /* Null: guess the kind from the text. */
            return(Vjs_ParseValue(sText));
        }

        /* A container has no text to convert. */
        return(null);
    }

    /*------------------------------------------------------------------------*/
    /* Vjs_LoadDocument:                                                      */
    /*                                                                        */
    /* A whole file's bytes to a document: decode (Vtx_Decode), parse         */
    /* (Vjs_Parse), and detect the style used for generated text (line        */
    /* ending, indentation unit, colon spacing).                              */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     bytes  : the file's contents.                                      */
    /*     sPath  : the file, for the document to remember; may be null.      */
    /*     sError : set to why the file is not valid JSON, else null.         */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesDocument : the document, or null when parsing failed.      */
    /*------------------------------------------------------------------------*/
    public static VoorheesDocument Vjs_LoadDocument(byte[] bytes, string sPath,
        out string sError)
    {
        Encoding encoding;                               // the file's encoding
        bool bBom;                                       // it had a byte order mark
        string sText;                                    // the decoded file
        VoorheesNode root;                               // the parsed document
        string sKeyGap;                                  // usual text between key and colon
        string sColonGap;                                // usual text between colon and value

        sText = VoorheesText.Vtx_Decode(bytes, out encoding, out bBom);
        root = Vjs_Parse(sText, out sError);
        if (root == null)
        {   /* Not valid JSON: sError says why. */
            return(null);
        }

        Vjs_DetectColonStyle(root, out sKeyGap, out sColonGap);

        /* The document with its file format and style. */
        return(VoorheesDocument.Vd_Create(VoorheesFormat.VF_JSON, root, sPath,
            encoding, bBom,
            VoorheesText.Vtx_DetectNewline(sText), Vjs_DetectIndentUnit(sText), sKeyGap,
            sColonGap));
    }

    /*------------------------------------------------------------------------*/
    /* Vjs_SaveBytes:                                                         */
    /*                                                                        */
    /* A document to the bytes of its file: written (Vjs_Write) and encoded   */
    /* as the file was (Vtx_Encode).                                          */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc : the document.                                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     byte[] : the file's new contents.                                  */
    /*------------------------------------------------------------------------*/
    public static byte[] Vjs_SaveBytes(VoorheesDocument doc)
    {
        /* Text first, then bytes in the file's own encoding. */
        return(VoorheesText.Vtx_Encode(Vjs_Write(doc), doc.Vd_encoding, doc.Vd_bBom));
    }

    /*------------------------------------------------------------------------*/
    /* Vjs_Parse:                                                             */
    /*                                                                        */
    /* Parses decoded text into a document node (VN_DOCUMENT): its top-level  */
    /* comment items, its one value item, and the text after them.            */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sText  : the decoded file.                                         */
    /*     sError : set to the reason, with line and column, when the text is */
    /*              not valid JSON (with comments); else null.                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesNode : the document node, or null on failure.              */
    /*------------------------------------------------------------------------*/
    public static VoorheesNode Vjs_Parse(string sText, out string sError)
    {
        VoorheesJsonParser parser;                       // the parse in progress
        VoorheesNode doc;                                // the document node

        parser = VoorheesJsonParser.Vjp_Create(sText, sText.Length);
        doc = VoorheesNode.Vn_NewContainer(VoorheesNode.VN_DOCUMENT);

        if (!parser.Vjp_ParseItems(doc, '\0'))
        {   /* Not valid: report where. */
            sError = parser.Vjp_DescribeError();
            return(null);
        }

        /* The document; Vn_bOneLine is not used for it. */
        sError = null;
        return(doc);
    }

    /*------------------------------------------------------------------------*/
    /* Vjs_ApplyRawEdit:                                                      */
    /*                                                                        */
    /* Builds the item a Raw edit describes: the text typed in the Raw box,   */
    /* parsed strictly as the same kind of item as the one being edited, and  */
    /* kept EXACTLY as typed.  What the Raw box does not show is carried      */
    /* over from the old item: its lead and -- unless the Raw box showed it   */
    /* (see Vjs_RenderItem) -- its end-of-line comment.  Leading and          */
    /* trailing whitespace in the box is not significant.                     */
    /*                                                                        */
    /* What is refused, with a reason: anything that is not valid JSON, a     */
    /* member without a key, a comment before the key (it belongs on a line   */
    /* of its own), a comma (Voorhees writes the commas), more than one item, */
    /* and for a comment item anything but comments.                          */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     oldItem : the item being edited.                                   */
    /*     sText   : the Raw box's text.                                      */
    /*     sError  : set to why the text is refused, with line and column     */
    /*               within the box; else null.                               */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesItem : the new item, or null when refused.                 */
    /*------------------------------------------------------------------------*/
    public static VoorheesItem Vjs_ApplyRawEdit(VoorheesItem oldItem,
        string sText, out string sError)
    {
        VoorheesJsonParser parser;                       // the parse of the box's text
        VoorheesItem item;                               // the item built
        int iEnd;                                        // end of the text, trailing whitespace dropped
        bool bRawHadEol;                                 // the Raw box showed the old end-of-line comment

        iEnd = sText.Length;
        while (iEnd > 0 && Vjs_IsWhitespace(sText[iEnd - 1]))
        {
            iEnd--;
        }

        parser = VoorheesJsonParser.Vjp_Create(sText, iEnd);
        item = parser.Vjp_ParseSnippet(oldItem.Vi_iKind);
        if (item == null)
        {   /* Refused: report where. */
            sError = parser.Vjp_DescribeError();
            return(null);
        }

        /* The lead is the item's place in its container, not part of Raw. */
        item.Vi_sLead = oldItem.Vi_sLead;

        bRawHadEol = Vjs_RawShowsEol(oldItem);
        if (item.Vi_IsValue() && !bRawHadEol && item.Vi_sEolComment == null)
        {   /* The old end-of-line comment was not in the box (it is the   */
            /* Comment box's), and the new text gives none: keep it.       */
            item.Vi_sEolGap = oldItem.Vi_sEolGap;
            item.Vi_sEolComment = oldItem.Vi_sEolComment;
        }

        item.Vi_bOddComments = Vjs_ComputeOdd(item);

        /* The item exactly as typed. */
        sError = null;
        return(item);
    }

    /*------------------------------------------------------------------------*/
    /* Vjs_RawShowsEol:                                                       */
    /*                                                                        */
    /* Whether an item's Raw text includes its end-of-line comment.  It does  */
    /* only for a container written over several lines, whose line comment    */
    /* is the one after its opening bracket: the comment after its closing    */
    /* bracket is then an odd comment, and Raw is the only place to edit it.  */
    /* For everything else the end-of-line comment is the Comment box's.      */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     item : the item.                                                   */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true when Raw shows the end-of-line comment.                */
    /*------------------------------------------------------------------------*/
    public static bool Vjs_RawShowsEol(VoorheesItem item)
    {
        /* Exactly when the line comment is the open one. */
        return(item.Vi_LineCommentIsOpen());
    }

    /*------------------------------------------------------------------------*/
    /* Vjs_ComputeOdd:                                                        */
    /*                                                                        */
    /* Works out an item's Vi_bOddComments from its fields: true when it has  */
    /* a comment that the Comment box (or, for a comment item, the Value box) */
    /* cannot edit:                                                           */
    /*     o a comment inside the key, colon or value gap;                    */
    /*     o several comments where the box edits one;                        */
    /*     o a comment on the side of a container that is not its line: an    */
    /*       end-of-line comment after a multi-line container's closing       */
    /*       bracket, or a comment after a one-line container's opening one.  */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     item : the item.                                                   */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : the value for Vi_bOddComments.                              */
    /*------------------------------------------------------------------------*/
    public static bool Vjs_ComputeOdd(VoorheesItem item)
    {
        if (item.Vi_iKind == VoorheesItem.VI_COMMENT)
        {   /* A comment item: odd when it holds more than one comment. */
            return(!Vjs_IsSingleComment(item.Vi_sComment));
        }

        if (Vjs_HasComment(item.Vi_sKeyGap)
            || Vjs_HasComment(item.Vi_sColonGap)
            || Vjs_HasComment(item.Vi_sValueGap))
        {   /* A comment inside the item's own text. */
            return(true);
        }

        if (item.Vi_LineCommentIsOpen())
        {   /* A multi-line container: its line comment is the open one. */
            if (item.Vi_sEolComment != null)
            {   /* A comment after its closing bracket: Raw only. */
                return(true);
            }

            /* Odd only if the open comment is several comments. */
            return(item.Vi_sOpenComment != null
                && !Vjs_IsSingleComment(item.Vi_sOpenComment));
        }

        if (item.Vi_sOpenComment != null)
        {   /* A comment after a one-line container's opening bracket: Raw only. */
            return(true);
        }

        /* Odd only if the end-of-line comment is several comments. */
        return(item.Vi_sEolComment != null
            && !Vjs_IsSingleComment(item.Vi_sEolComment));
    }

    /*------------------------------------------------------------------------*/
    /* Vjs_FinalizeItem:                                                      */
    /*                                                                        */
    /* JSON's last word on an item an editor action is about to put into a    */
    /* document (VoorheesDocument.Vd_Finalize): its odd-comment flag is       */
    /* worked out afresh from its fields, since the action may have changed   */
    /* its comments or turned its value into or out of a container.  The      */
    /* item is not in a document yet, so it is changed in place.              */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     item : the item about to go in.                                    */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesItem : the same item, its flag set.                        */
    /*------------------------------------------------------------------------*/
    public static VoorheesItem Vjs_FinalizeItem(VoorheesItem item)
    {
        item.Vi_bOddComments = Vjs_ComputeOdd(item);

        /* Ready to apply. */
        return(item);
    }

    /*------------------------------------------------------------------------*/
    /* Vjs_ResetLayout:                                                       */
    /*                                                                        */
    /* Clears an item's layout, and that of everything inside its value, so   */
    /* the writer lays it out afresh in the document's style: leads,          */
    /* end-of-line and open gaps, and containers' closing text are            */
    /* generated; the key, colon and value gaps too unless they hold a        */
    /* comment, which is kept whole.  Kept: keys and values (with their raw   */
    /* tokens, which are spelling, not layout), every comment, and            */
    /* Vn_bOneLine.  Used on a pasted copy, whose old layout belonged to      */
    /* wherever it was copied from.                                           */
    /*                                                                        */
    /* ONLY for a copy that is not (yet) in a document: it changes items in   */
    /* place, which is never done to items in a document.                     */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     item : the copy.                                                   */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : its layout fields are null where they were plain            */
    /*            whitespace.                                                 */
    /*------------------------------------------------------------------------*/
    public static void Vjs_ResetLayout(VoorheesItem item)
    {
        VoorheesNode node;                               // the item's value, if it has one
        int i;

        item.Vi_sLead = null;
        item.Vi_sEolGap = null;
        item.Vi_sOpenGap = null;

        if (!Vjs_HasComment(item.Vi_sKeyGap))
        {   /* Plain whitespace: use the document's colon style. */
            item.Vi_sKeyGap = null;
        }

        if (!Vjs_HasComment(item.Vi_sColonGap))
        {   /* Plain whitespace: use the document's colon style. */
            item.Vi_sColonGap = null;
        }

        if (!Vjs_HasComment(item.Vi_sValueGap))
        {   /* Plain whitespace: nothing between the value and its comma. */
            item.Vi_sValueGap = null;
        }

        node = item.Vi_node;
        if (node == null || node.Vn_lstItems == null)
        {   /* A comment or a plain value: nothing inside to lay out. */
            return;
        }

        /* A container: its closing text is only ever whitespace, so it is   */
        /* generated, and everything inside is laid out afresh too.          */
        node.Vn_sClose = null;
        for (i = 0; i < node.Vn_lstItems.Count; i++)
        {
            Vjs_ResetLayout(node.Vn_lstItems[i]);
        }
    }

    /*------------------------------------------------------------------------*/
    /* Vjs_Write:                                                             */
    /*                                                                        */
    /* The document as text: every stored string exactly as it is, and        */
    /* everything left null generated in the document's style.                */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc : the document.                                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the file's text (without a byte order mark).              */
    /*------------------------------------------------------------------------*/
    public static string Vjs_Write(VoorheesDocument doc)
    {
        VoorheesJsonWriter writer;                       // the write in progress

        writer = VoorheesJsonWriter.Vjw_Create(doc, "");
        writer.Vjw_WriteContainer(doc.Vd_root, null);

        /* All of it. */
        return(writer.Vjw_Result(0));
    }

    /*------------------------------------------------------------------------*/
    /* Vjs_RenderItem:                                                        */
    /*                                                                        */
    /* One item's text for the Raw box and the raw tree labels: its content   */
    /* exactly as it would be written (see the VoorheesItem class header) --  */
    /* for a member the key, the colon and the value with every gap; for a    */
    /* comment item the comment -- without its lead or comma.  For a          */
    /* multi-line container the end-of-line comment is added at the end       */
    /* (Vjs_RawShowsEol).  Generated parts are indented as they would be in   */
    /* the file: the item's line indentation is worked out from the leads on  */
    /* the way down.                                                          */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc     : the document.                                            */
    /*     lstPath : the item's position path (not empty).                    */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the item's text, or "" when the path names no item.       */
    /*------------------------------------------------------------------------*/
    public static string Vjs_RenderItem(VoorheesDocument doc, List<int> lstPath)
    {
        bool bTruncated;                                 // never true without a limit

        /* No limit: the whole item. */
        return(Vjs_RenderItemLimited(doc, lstPath, int.MaxValue, out bTruncated));
    }

    /*------------------------------------------------------------------------*/
    /* Vjs_RenderItemLimited:                                                 */
    /*                                                                        */
    /* Vjs_RenderItem, stopping once a given number of characters has been    */
    /* written, so the tree's raw labels and the Raw box can show the start   */
    /* of a huge container without writing all of it.                         */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc        : the document.                                         */
    /*     lstPath    : the item's position path (not empty).                 */
    /*     iMaxChars  : most characters wanted (int.MaxValue for no limit).   */
    /*     bTruncated : set to true when the item is longer than iMaxChars.   */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the item's text, at most iMaxChars long; "" when the path */
    /*              names no item.                                            */
    /*------------------------------------------------------------------------*/
    public static string Vjs_RenderItemLimited(VoorheesDocument doc,
        List<int> lstPath, int iMaxChars, out bool bTruncated)
    {
        VoorheesJsonWriter writer;                       // the write in progress
        VoorheesNode container;                          // the container at each level
        VoorheesItem item;                               // the item at each level
        string sIndent;                                  // indentation of the line the item at this level starts on
        int iNewline;                                    // last line break in a lead
        int i;

        sIndent = "";
        container = doc.Vd_root;
        item = null;
        bTruncated = false;

        /* Walk down, working out each level's line indentation from the    */
        /* lead: after its last line break if it has one, one unit deeper   */
        /* than the container's if it will be generated in a multi-line     */
        /* container, otherwise the same line as the container's.           */
        for (i = 0; i < lstPath.Count; i++)
        {
            if (container == null || !container.Vn_IsContainer()
                || lstPath[i] < 0 || lstPath[i] >= container.Vn_Count())
            {   /* The path leads nowhere. */
                return("");
            }

            item = container.Vn_lstItems[lstPath[i]];
            if (item.Vi_sLead != null)
            {   /* Explicit lead: a line break in it starts a new line. */
                iNewline = item.Vi_sLead.LastIndexOf('\n');
                if (iNewline >= 0)
                {   /* The item starts a line: its indentation is what follows. */
                    sIndent = item.Vi_sLead.Substring(iNewline + 1);
                }
            }
            else if (container.Vn_iKind == VoorheesNode.VN_DOCUMENT)
            {   /* Generated top-level lead: column one. */
                sIndent = "";
            }
            else if (!container.Vn_bOneLine)
            {   /* Generated lead in a multi-line container: one level deeper. */
                sIndent = sIndent + doc.Vd_sIndentUnit;
            }

            container = item.Vi_node;
        }

        if (item == null)
        {   /* The empty path: the document is not an item. */
            return("");
        }

        /* Write the item after its line's indentation, then drop that.    */
        /* The writer stops once it is past the limit (prefix included).   */
        writer = VoorheesJsonWriter.Vjw_Create(doc, sIndent);
        if (iMaxChars < int.MaxValue - sIndent.Length)
        {   /* A limit: count it from after the prefix. */
            writer.Vjw_SetLimit(sIndent.Length + iMaxChars);
        }

        writer.Vjw_WriteItemContent(item);
        if (item.Vi_IsValue() && Vjs_RawShowsEol(item)
            && item.Vi_sEolComment != null)
        {   /* A multi-line container's end-of-line comment: Raw is its only editor. */
            writer.Vjw_PutText(Vjs_GapOrDefault(item.Vi_sEolGap));
            writer.Vjw_PutTrivia(item.Vi_sEolComment);
        }

        bTruncated = writer.Vjw_Stopped();

        /* Everything after the indentation prefix, at most iMaxChars of it. */
        return(writer.Vjw_ResultLimited(sIndent.Length, iMaxChars));
    }

    /*------------------------------------------------------------------------*/
    /* Vjs_GapOrDefault:                                                      */
    /*                                                                        */
    /* The whitespace before a comment: the stored gap, or the generated      */
    /* default when it is null.                                               */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sGap : the stored gap, or null.                                    */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the gap to write.                                         */
    /*------------------------------------------------------------------------*/
    public static string Vjs_GapOrDefault(string sGap)
    {
        if (sGap == null)
        {   /* Generate: the default spacing. */
            return(VJS_DEFAULTCOMMENTGAP);
        }

        /* As stored. */
        return(sGap);
    }

    /*------------------------------------------------------------------------*/
    /* Vjs_QuoteString:                                                       */
    /*                                                                        */
    /* A string token for a decoded string: double quotes around it, with     */
    /* the minimum escaping JSON needs -- \" and \\, the short forms \b \f    */
    /* \n \r \t, \u00XX for any other control character -- and every other    */
    /* character written as itself.  A string read from a file without        */
    /* escapes comes out exactly as it was written, which is why the reader   */
    /* stores no raw token for one.                                           */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sText : the decoded string.                                        */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the token, quotes included.                               */
    /*------------------------------------------------------------------------*/
    public static string Vjs_QuoteString(string sText)
    {
        StringBuilder sb;                                // the token being built
        char c;                                          // character being written
        int i;

        sb = new StringBuilder(sText.Length + 2);
        sb.Append('"');
        for (i = 0; i < sText.Length; i++)
        {
            c = sText[i];
            switch (c)
            {
                case '"':  sb.Append("\\\""); break;     // quote
                case '\\': sb.Append("\\\\"); break;     // backslash
                case '\b': sb.Append("\\b"); break;      // backspace
                case '\f': sb.Append("\\f"); break;      // form feed
                case '\n': sb.Append("\\n"); break;      // line feed
                case '\r': sb.Append("\\r"); break;      // carriage return
                case '\t': sb.Append("\\t"); break;      // tab
                default:
                    if (c < 0x20)
                    {   /* Any other control character: a \u escape. */
                        sb.Append("\\u");
                        sb.Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {   /* Everything else as itself. */
                        sb.Append(c);
                    }
                    break;
            }
        }
        sb.Append('"');

        /* The quoted token. */
        return(sb.ToString());
    }

    /*------------------------------------------------------------------------*/
    /* Vjs_IsWhitespace:                                                      */
    /*                                                                        */
    /* JSON's whitespace: space, tab, carriage return, line feed.             */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     c : the character.                                                 */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true for one of the four.                                   */
    /*------------------------------------------------------------------------*/
    public static bool Vjs_IsWhitespace(char c)
    {
        /* Exactly RFC 8259's four. */
        return(c == ' ' || c == '\t' || c == '\r' || c == '\n');
    }

    /*------------------------------------------------------------------------*/
    /* Vjs_HasComment:                                                        */
    /*                                                                        */
    /* Whether a gap holds a comment.  Gaps hold only whitespace and          */
    /* comments, so any slash means a comment.                                */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sGap : the gap, or null.                                           */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true when it contains a comment.                            */
    /*------------------------------------------------------------------------*/
    public static bool Vjs_HasComment(string sGap)
    {
        /* Null and whitespace-only gaps have no slash. */
        return(sGap != null && sGap.IndexOf('/') >= 0);
    }

    /*------------------------------------------------------------------------*/
    /* Vjs_EndsInLineComment:                                                 */
    /*                                                                        */
    /* Whether text made of whitespace and comments ends inside a // comment  */
    /* -- that is, whether whatever is written next must start on a new line  */
    /* or it would be swallowed by the comment.  Block comments are skipped   */
    /* whole (a // inside one is not a comment).                              */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     s : the text (a gap or a comment).                                 */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true when a line comment runs to its end.                   */
    /*------------------------------------------------------------------------*/
    public static bool Vjs_EndsInLineComment(string s)
    {
        int i;
        int iEnd;                                        // end of a comment

        i = 0;
        while (i < s.Length - 1)
        {
            if (s[i] == '/' && s[i + 1] == '/')
            {   /* A line comment: runs to the next line break, or to the end. */
                iEnd = s.IndexOf('\n', i + 2);
                if (iEnd < 0)
                {   /* No line break after it: the text ends inside it. */
                    return(true);
                }
                i = iEnd + 1;
            }
            else if (s[i] == '/' && s[i + 1] == '*')
            {   /* A block comment: skip to its end. */
                iEnd = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (iEnd < 0)
                {   /* Unterminated (never stored, but be safe): no line comment open. */
                    return(false);
                }
                i = iEnd + 2;
            }
            else
            {   /* Whitespace. */
                i++;
            }
        }

        /* Ended outside any line comment. */
        return(false);
    }

    /*------------------------------------------------------------------------*/
    /* Vjs_IsSingleComment:                                                   */
    /*                                                                        */
    /* Whether stored comment text is exactly one comment: a // comment, or   */
    /* a block comment ending at the very end.                                */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sRaw : comment text as stored (starts with its first comment).     */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true for one comment; false for several (or none).          */
    /*------------------------------------------------------------------------*/
    public static bool Vjs_IsSingleComment(string sRaw)
    {
        int iEnd;                                        // where the first block comment ends

        if (sRaw == null || sRaw.Length < 2 || sRaw[0] != '/')
        {   /* Not a comment at all. */
            return(false);
        }

        if (sRaw[1] == '/')
        {   /* A line comment runs to the end of its line: one comment if no line break follows. */
            return(sRaw.IndexOf('\n') < 0);
        }

        if (sRaw[1] == '*')
        {   /* A block comment: one comment if it ends at the end. */
            iEnd = sRaw.IndexOf("*/", 2, StringComparison.Ordinal);
            return(iEnd == sRaw.Length - 2);
        }

        /* A slash and something else: not a comment. */
        return(false);
    }

    /*------------------------------------------------------------------------*/
    /* Vjs_CommentText:                                                       */
    /*                                                                        */
    /* The text of a comment without its markers, as the tree and the         */
    /* Comment and Value boxes show it: "// note" and "/* note *-/" both give */
    /* "note" (one space after the opening marker, and before a block's       */
    /* closing one, is taken as part of the marker).  Several comments in     */
    /* one are shown as they are.                                             */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sRaw : comment text as stored.                                     */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the text; "" for null.                                    */
    /*------------------------------------------------------------------------*/
    public static string Vjs_CommentText(string sRaw)
    {
        string sText;                                    // the text between the markers

        if (sRaw == null)
        {   /* No comment: no text. */
            return("");
        }

        if (!Vjs_IsSingleComment(sRaw))
        {   /* Several comments: shown whole. */
            return(sRaw);
        }

        if (sRaw[1] == '/')
        {   /* Line comment: after the // and one space. */
            sText = sRaw.Substring(2);
            if (sText.StartsWith(" "))
            {   /* The space after the marker. */
                sText = sText.Substring(1);
            }
            return(sText);
        }

        /* Block comment: between the markers, one space trimmed each side. */
        sText = sRaw.Substring(2, sRaw.Length - 4);
        if (sText.StartsWith(" "))
        {   /* The space after the opening marker. */
            sText = sText.Substring(1);
        }

        if (sText.EndsWith(" "))
        {   /* The space before the closing marker. */
            sText = sText.Substring(0, sText.Length - 1);
        }

        /* The comment's words. */
        return(sText);
    }

    /*------------------------------------------------------------------------*/
    /* Vjs_IsBlockComment:                                                    */
    /*                                                                        */
    /* Whether stored comment text starts with a block comment.               */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sRaw : comment text as stored.                                     */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true for a block comment, false for a line comment or none. */
    /*------------------------------------------------------------------------*/
    public static bool Vjs_IsBlockComment(string sRaw)
    {
        /* Slash, star. */
        return(sRaw != null && sRaw.Length >= 2 && sRaw[0] == '/'
            && sRaw[1] == '*');
    }

    /*------------------------------------------------------------------------*/
    /* Vjs_MakeComment:                                                       */
    /*                                                                        */
    /* A comment from plain text: "// text", or a block comment when one is   */
    /* asked for or the text has a line break (a line comment cannot hold     */
    /* one).  Empty text gives an empty comment.                              */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sText  : the comment's words.                                      */
    /*     bBlock : prefer a block comment (for an item whose line goes on    */
    /*              after the comment, such as one in a one-line container).  */
    /*     sError : set to why the text cannot be a comment, else null.       */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the comment with its markers, or null when refused (a     */
    /*              block comment cannot contain its own closing marker).     */
    /*------------------------------------------------------------------------*/
    public static string Vjs_MakeComment(string sText, bool bBlock,
        out string sError)
    {
        sError = null;
        if (sText.IndexOf('\n') >= 0 || sText.IndexOf('\r') >= 0)
        {   /* Several lines: only a block comment can hold them. */
            bBlock = true;
        }

        if (!bBlock)
        {   /* A line comment. */
            if (sText.Length == 0)
            {   /* Empty. */
                return("//");
            }
            return("// " + sText);
        }

        if (sText.IndexOf("*/", StringComparison.Ordinal) >= 0)
        {   /* The closing marker would end the comment early. */
            sError = "A block comment can't contain \"*/\".";
            return(null);
        }

        if (sText.Length == 0)
        {   /* Empty. */
            return("/**/");
        }

        /* A block comment. */
        return("/* " + sText + " */");
    }

    /*------------------------------------------------------------------------*/
    /* Vjs_RemakeComment:                                                     */
    /*                                                                        */
    /* An existing comment with new words, in the same style: unchanged words */
    /* give back the comment exactly as it was; otherwise a line comment      */
    /* stays a line comment and a block comment a block comment (a line       */
    /* comment that gains a line break becomes a block one).                  */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sOldRaw : the comment as stored.                                   */
    /*     sText   : its new words.                                           */
    /*     sError  : set to why they cannot be a comment, else null.          */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the comment, or null when refused.                        */
    /*------------------------------------------------------------------------*/
    public static string Vjs_RemakeComment(string sOldRaw, string sText,
        out string sError)
    {
        if (sText == Vjs_CommentText(sOldRaw))
        {   /* Same words: keep the comment exactly as written. */
            sError = null;
            return(sOldRaw);
        }

        /* New words in the old style. */
        return(Vjs_MakeComment(sText, Vjs_IsBlockComment(sOldRaw), out sError));
    }

    /*------------------------------------------------------------------------*/
    /* Vjs_MakeCommentAt:                                                     */
    /*                                                                        */
    /* A NEW comment for a place in a container, from its words: "// words"   */
    /* unless something would follow it on the same line, which a line        */
    /* comment would swallow, when it is a block comment instead:             */
    /*     o the comment on the line of the item at iPos (bLineComment): the  */
    /*       line goes on when Vjs_LineGoesOn says so for that side of the    */
    /*       item (after its opening bracket for a multi-line container,      */
    /*       else at the end of its line);                                    */
    /*     o a new comment ITEM added at the end of the container (iPos past  */
    /*       its last item): the line goes on only inside a container         */
    /*       written on one line (the document never is).                     */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     container    : the container.                                      */
    /*     iPos         : the item whose line it is (bLineComment), or where  */
    /*                    the comment item goes.                              */
    /*     sText        : the words.                                          */
    /*     bLineComment : true for an item's line comment, false for a        */
    /*                    comment item.                                       */
    /*     sError       : set to why the words cannot be a comment, else      */
    /*                    null.                                               */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the comment with its markers, or null when refused.       */
    /*------------------------------------------------------------------------*/
    public static string Vjs_MakeCommentAt(VoorheesNode container, int iPos,
        string sText, bool bLineComment, out string sError)
    {
        bool bBlock;                                     // something follows on the same line

        if (bLineComment)
        {   /* An item's line comment: does its line go on after it? */
            bBlock = Vjs_LineGoesOn(container, iPos,
                container.Vn_lstItems[iPos].Vi_LineCommentIsOpen());
        }
        else if (iPos >= container.Vn_Count())
        {   /* A new comment item at the end: generated layout puts a line   */
            /* break after it unless the container is on one line.           */
            bBlock = container.Vn_bOneLine
                && container.Vn_iKind != VoorheesNode.VN_DOCUMENT;
        }
        else
        {   /* A comment item at an existing position: what comes after it. */
            bBlock = Vjs_LineGoesOn(container, iPos, false);
        }

        /* "// words", or a block comment where the line goes on. */
        return(Vjs_MakeComment(sText, bBlock, out sError));
    }

    /*------------------------------------------------------------------------*/
    /* Vjs_RemakeCommentAt:                                                   */
    /*                                                                        */
    /* An existing comment at a place in a container with new words, in its   */
    /* own style (Vjs_RemakeComment).  For a comment ITEM one more check: if  */
    /* the result is a line comment but the line goes on after it             */
    /* (Vjs_LineGoesOn), it is made a block comment so it swallows nothing.   */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     container    : the container.                                      */
    /*     iPos         : the item (whose line comment it is, or the comment  */
    /*                    item itself).                                       */
    /*     sOldRaw      : the comment as stored.                              */
    /*     sText        : its new words.                                      */
    /*     bLineComment : true for an item's line comment, false for a        */
    /*                    comment item.                                       */
    /*     sError       : set to why the words cannot be a comment, else      */
    /*                    null.                                               */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the comment, or null when refused.                        */
    /*------------------------------------------------------------------------*/
    public static string Vjs_RemakeCommentAt(VoorheesNode container, int iPos,
        string sOldRaw, string sText, bool bLineComment, out string sError)
    {
        string sNew;                                     // the comment in its own style

        sNew = Vjs_RemakeComment(sOldRaw, sText, out sError);
        if (sNew == null || bLineComment)
        {   /* Refused, or a line comment: that is the answer. */
            return(sNew);
        }

        if (!Vjs_IsBlockComment(sNew) && Vjs_LineGoesOn(container, iPos, false))
        {   /* A line comment would swallow what follows it: use a block comment. */
            sNew = Vjs_MakeComment(sText, true, out sError);
        }

        /* The comment item's new text (or null when refused). */
        return(sNew);
    }

    /*------------------------------------------------------------------------*/
    /* Vjs_LineGoesOn:                                                        */
    /*                                                                        */
    /* Whether something follows on the same line after an item's comment     */
    /* would go: after its end-of-line comment (or, for a multi-line          */
    /* container, after its opening bracket), is the next text written a line */
    /* break?  If not, a // comment there would swallow it, so a new comment  */
    /* is made a block comment instead.                                       */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     container : the item's container.                                  */
    /*     iPos      : the item's position.                                   */
    /*     bOpen     : the comment goes after the item's opening bracket      */
    /*                 rather than at the end of its line.                    */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true when the line goes on after the comment.               */
    /*------------------------------------------------------------------------*/
    public static bool Vjs_LineGoesOn(VoorheesNode container, int iPos,
        bool bOpen)
    {
        VoorheesNode inner;                              // the item's own container (bOpen)
        string sNext;                                    // the text that comes next, if stored

        if (bOpen)
        {   /* After the opening bracket: the first inner item's lead, or the close. */
            inner = container.Vn_lstItems[iPos].Vi_node;
            if (inner.Vn_Count() > 0)
            {   /* The first item's lead. */
                sNext = inner.Vn_lstItems[0].Vi_sLead;
            }
            else
            {   /* No items: the closing text. */
                sNext = inner.Vn_sClose;
            }

            if (sNext == null)
            {   /* Generated: multi-line containers put a line break there. */
                return(inner.Vn_bOneLine);
            }
            return(!Vjs_StartsLine(sNext));
        }

        /* At the end of the item's line: the next item's lead, or the close. */
        if (iPos + 1 < container.Vn_Count())
        {   /* The next item's lead. */
            sNext = container.Vn_lstItems[iPos + 1].Vi_sLead;
        }
        else if (container.Vn_iKind == VoorheesNode.VN_DOCUMENT)
        {   /* The last thing in the file: only whitespace follows, never a token. */
            return(false);
        }
        else
        {   /* The last item: the closing text, then the bracket. */
            sNext = container.Vn_sClose;
        }

        if (sNext == null)
        {   /* Generated: a line break unless the container is on one line. */
            return(container.Vn_bOneLine
                && container.Vn_iKind != VoorheesNode.VN_DOCUMENT);
        }

        /* Stored: does it start a new line? */
        return(!Vjs_StartsLine(sNext));
    }

    /*------------------------------------------------------------------------*/
    /* Vjs_StartsLine:                                                        */
    /*                                                                        */
    /* Whether text starts with a line break.                                 */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     s : the text.                                                      */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true when nothing follows on the same line.                 */
    /*------------------------------------------------------------------------*/
    static bool Vjs_StartsLine(string s)
    {
        /* A carriage return or line feed first. */
        return(s.Length > 0 && (s[0] == '\r' || s[0] == '\n'));
    }

    /*------------------------------------------------------------------------*/
    /* Vjs_DetectIndentUnit:                                                  */
    /*                                                                        */
    /* One level of a file's indentation, for generated lines.  Every line    */
    /* after the first is looked at:                                          */
    /*     o if more indented lines start with a tab than with a space, one   */
    /*       level is one tab;                                                */
    /*     o otherwise one level is the SMALLEST run of leading spaces on any */
    /*       line, which is a top-level member's indentation;                 */
    /*     o a file with no indented lines (all on one line, say), or whose   */
    /*       smallest indent is wider than VJS_MAXINDENTSPACES, gives null so */
    /*       the document takes its default.                                  */
    /* JSON strings cannot hold a raw line break, so a line's start is always */
    /* layout (or the inside of a block comment, which is rare enough not to  */
    /* skew the count).                                                       */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sText : the decoded file.                                          */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : "\t", 1 to VJS_MAXINDENTSPACES spaces, or null.           */
    /*------------------------------------------------------------------------*/
    public static string Vjs_DetectIndentUnit(string sText)
    {
        int iTabLines;                                   // indented lines that start with a tab
        int iSpaceLines;                                 // indented lines that start with a space
        int iMinSpaces;                                  // smallest leading-space run seen
        int iLead;                                       // leading spaces on this line
        int iPos;                                        // start of the line being looked at
        int iNext;                                       // next line feed

        iTabLines = 0;
        iSpaceLines = 0;
        iMinSpaces = int.MaxValue;

        /* Line 0 holds the opening bracket and is never indented: start   */
        /* after the first line feed.                                      */
        iNext = sText.IndexOf('\n');
        while (iNext >= 0)
        {
            iPos = iNext + 1;
            iNext = sText.IndexOf('\n', iPos);

            if (iPos < sText.Length && sText[iPos] == '\t')
            {   /* Tab-indented line. */
                iTabLines++;
            }
            else if (iPos < sText.Length && sText[iPos] == ' ')
            {   /* Space-indented line: count its leading spaces. */
                iLead = 0;
                while (iPos + iLead < sText.Length && sText[iPos + iLead] == ' ')
                {
                    iLead++;
                }

                if (iPos + iLead < sText.Length && !Vjs_IsWhitespace(sText[iPos + iLead]))
                {   /* Spaces then text: a real indented line (blank ones say nothing). */
                    iSpaceLines++;
                    if (iLead < iMinSpaces)
                    {   /* Shallowest so far: the best candidate for one level. */
                        iMinSpaces = iLead;
                    }
                }
            }
        }

        if (iTabLines > iSpaceLines)
        {   /* Mostly tabs: one level is one tab. */
            return("\t");
        }

        if (iSpaceLines > 0 && iMinSpaces <= VJS_MAXINDENTSPACES)
        {   /* Space-indented with a believable step: use it. */
            return(new string(' ', iMinSpaces));
        }

        /* No usable indentation to copy: the document's default. */
        return(null);
    }

    /*------------------------------------------------------------------------*/
    /* Vjs_DetectColonStyle:                                                  */
    /*                                                                        */
    /* How a file spaces its colons, for generated members: the most common   */
    /* pair of key gap and colon gap among its members whose gaps are plain   */
    /* spaces (no comment, no line break), so "key": value, "key" : value     */
    /* and "key":value files each get more of the same.                       */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     root      : the parsed document node.                              */
    /*     sKeyGap   : set to the usual text between key and colon, or null.  */
    /*     sColonGap : set to the usual text between colon and value, or      */
    /*                 null.  Both null when the file has no such members.    */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : results through the out arguments.                          */
    /*------------------------------------------------------------------------*/
    public static void Vjs_DetectColonStyle(VoorheesNode root,
        out string sKeyGap, out string sColonGap)
    {
        Dictionary<string, int> dictCounts;              // "keygap|colongap" to members using it
        string sBest;                                    // the most used pair so far
        int iBest;                                       // how many use it
        int iBar;                                        // where the pair splits

        dictCounts = new Dictionary<string, int>();
        Vjs_CountColonStyles(root, dictCounts);

        sBest = null;
        iBest = 0;
        foreach (KeyValuePair<string, int> kv in dictCounts)
        {
            if (kv.Value > iBest)
            {   /* More common than any seen so far. */
                sBest = kv.Key;
                iBest = kv.Value;
            }
        }

        if (sBest == null)
        {   /* No members to go by: the document's default. */
            sKeyGap = null;
            sColonGap = null;
            return;
        }

        iBar = sBest.IndexOf('|');
        sKeyGap = sBest.Substring(0, iBar);
        sColonGap = sBest.Substring(iBar + 1);
    }

    /*------------------------------------------------------------------------*/
    /* Vjs_CountColonStyles:                                                  */
    /*                                                                        */
    /* Vjs_DetectColonStyle's walk: tallies the gap pairs of every member in  */
    /* a node and everything inside it.                                       */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     node       : the node.                                             */
    /*     dictCounts : "keygap|colongap" to count, added to.                 */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the tally is updated.                                       */
    /*------------------------------------------------------------------------*/
    static void Vjs_CountColonStyles(VoorheesNode node,
        Dictionary<string, int> dictCounts)
    {
        VoorheesItem item;                               // an item of the node
        string sPair;                                    // its gap pair
        int iCount;                                      // the pair's count so far
        int i;

        if (!node.Vn_IsContainer())
        {   /* A plain value has no members. */
            return;
        }

        for (i = 0; i < node.Vn_lstItems.Count; i++)
        {
            item = node.Vn_lstItems[i];
            if (item.Vi_iKind == VoorheesItem.VI_MEMBER && item.Vi_sKeyGap != null
                && item.Vi_sColonGap != null && Vjs_IsSpaces(item.Vi_sKeyGap)
                && Vjs_IsSpaces(item.Vi_sColonGap))
            {   /* A member with plain spacing: tally it. */
                sPair = item.Vi_sKeyGap + "|" + item.Vi_sColonGap;
                if (dictCounts.TryGetValue(sPair, out iCount))
                {   /* Seen before. */
                    dictCounts[sPair] = iCount + 1;
                }
                else
                {   /* First time. */
                    dictCounts[sPair] = 1;
                }
            }

            if (item.Vi_IsValue())
            {   /* Members inside its value too. */
                Vjs_CountColonStyles(item.Vi_node, dictCounts);
            }
        }
    }

    /*------------------------------------------------------------------------*/
    /* Vjs_IsSpaces:                                                          */
    /*                                                                        */
    /* Whether text is only spaces and tabs (or empty).                       */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     s : the text.                                                      */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true when nothing but spaces and tabs.                      */
    /*------------------------------------------------------------------------*/
    static bool Vjs_IsSpaces(string s)
    {
        int i;

        for (i = 0; i < s.Length; i++)
        {
            if (s[i] != ' ' && s[i] != '\t')
            {   /* Anything else. */
                return(false);
            }
        }

        /* Spaces and tabs only. */
        return(true);
    }
}

/*----------------------------------------------------------------------------*/
/* VoorheesJsonParser                                                         */
/*                                                                            */
/* One parse over one string, index-based.  Every Vjp_ function returns       */
/* false (or null) on the first error, after recording it with Vjp_Fail;      */
/* the callers stop at once and the error position and message are            */
/* reported by Vjp_DescribeError.  Repeated trivia and keys are interned      */
/* through a dictionary local to the parse, so 40,000 copies of "\n    "      */
/* or of a key are one string in memory.                                      */
/*                                                                            */
/* Line breaks outside tokens are counted as they are passed (tokens cannot   */
/* hold raw line breaks), which is how a container learns cheaply whether it  */
/* was written on one line.                                                   */
/*----------------------------------------------------------------------------*/
class VoorheesJsonParser
{
    string Vjp_s;                                        // the text being parsed
    int Vjp_i;                                           // current position
    int Vjp_n;                                           // end of the text to parse (may be before Vjp_s.Length)
    int Vjp_iNewlines;                                   // line feeds passed outside tokens so far
    int Vjp_iDepth;                                      // containers currently open
    Dictionary<string, string> Vjp_dictIntern;           // one copy of each repeated trivia string or key
    string Vjp_sError;                                   // the first error, or null
    int Vjp_iErrorPos;                                   // where it was found

    /*------------------------------------------------------------------------*/
    /* Vjp_Create:                                                            */
    /*                                                                        */
    /* A parser over a string.                                                */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sText : the text.                                                  */
    /*     iEnd  : where parsing stops (sText.Length for a whole file; a Raw  */
    /*             edit stops before its trailing whitespace).                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesJsonParser : positioned at the start.                      */
    /*------------------------------------------------------------------------*/
    public static VoorheesJsonParser Vjp_Create(string sText, int iEnd)
    {
        VoorheesJsonParser parser;                       // the new parser

        parser = new VoorheesJsonParser();
        parser.Vjp_s = sText;
        parser.Vjp_i = 0;
        parser.Vjp_n = iEnd;
        parser.Vjp_iNewlines = 0;
        parser.Vjp_iDepth = 0;
        parser.Vjp_dictIntern = new Dictionary<string, string>();
        parser.Vjp_sError = null;
        parser.Vjp_iErrorPos = 0;

        /* Ready. */
        return(parser);
    }

    /*------------------------------------------------------------------------*/
    /* Vjp_Fail:                                                              */
    /*                                                                        */
    /* Records an error (only the first one counts: it is the real one).      */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iPos     : where in the text.                                      */
    /*     sMessage : what is wrong, for the user.                            */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : always false, so callers can "return(Vjp_Fail(...))".       */
    /*------------------------------------------------------------------------*/
    bool Vjp_Fail(int iPos, string sMessage)
    {
        if (Vjp_sError == null)
        {   /* The first error: keep it. */
            Vjp_sError = sMessage;
            Vjp_iErrorPos = iPos;
        }

        /* Failure, for the caller to pass up. */
        return(false);
    }

    /*------------------------------------------------------------------------*/
    /* Vjp_DescribeError:                                                     */
    /*                                                                        */
    /* The recorded error with its line and column (both from 1; a tab        */
    /* counts as one column).                                                 */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : e.g. "Line 3, column 7: Expected ':' after the key."      */
    /*------------------------------------------------------------------------*/
    public string Vjp_DescribeError()
    {
        int iLine;                                       // line of the error
        int iLineStart;                                  // where that line starts
        int i;

        iLine = 1;
        iLineStart = 0;
        for (i = 0; i < Vjp_iErrorPos && i < Vjp_s.Length; i++)
        {
            if (Vjp_s[i] == '\n')
            {   /* A new line starts after this. */
                iLine++;
                iLineStart = i + 1;
            }
        }

        /* Where, then what. */
        return("Line " + iLine.ToString(CultureInfo.InvariantCulture)
            + ", column " + (Vjp_iErrorPos - iLineStart + 1).ToString(
            CultureInfo.InvariantCulture) + ": " + Vjp_sError);
    }

    /*------------------------------------------------------------------------*/
    /* Vjp_Intern:                                                            */
    /*                                                                        */
    /* A piece of the text as a string, one shared copy per distinct value.   */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iStart : first character.                                          */
    /*     iEnd   : one past the last.                                        */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the piece; "" when empty.                                 */
    /*------------------------------------------------------------------------*/
    string Vjp_Intern(int iStart, int iEnd)
    {
        if (iEnd <= iStart)
        {   /* Nothing between: the empty string. */
            return("");
        }

        /* The piece, shared with every equal piece. */
        return(Vjp_InternString(Vjp_s.Substring(iStart, iEnd - iStart)));
    }

    /*------------------------------------------------------------------------*/
    /* Vjp_InternString:                                                      */
    /*                                                                        */
    /* The shared copy of a string: the first one seen of each value.         */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     s : a string.                                                      */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the shared copy.                                          */
    /*------------------------------------------------------------------------*/
    string Vjp_InternString(string s)
    {
        string sShared;                                  // the copy already kept

        if (Vjp_dictIntern.TryGetValue(s, out sShared))
        {   /* Seen before: use that copy. */
            return(sShared);
        }

        Vjp_dictIntern[s] = s;

        /* The first of its value: this becomes the shared copy. */
        return(s);
    }

    /*------------------------------------------------------------------------*/
    /* Vjp_IsCommentStart:                                                    */
    /*                                                                        */
    /* Whether a comment starts at a position: "//" or "/*".                  */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iPos : the position.                                               */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true at the start of a comment.                             */
    /*------------------------------------------------------------------------*/
    bool Vjp_IsCommentStart(int iPos)
    {
        /* A slash followed by a slash or a star. */
        return(iPos + 1 < Vjp_n && Vjp_s[iPos] == '/'
            && (Vjp_s[iPos + 1] == '/' || Vjp_s[iPos + 1] == '*'));
    }

    /*------------------------------------------------------------------------*/
    /* Vjp_SkipComment:                                                       */
    /*                                                                        */
    /* Passes the comment at the current position: a line comment up to (not  */
    /* including) its line break, a block comment through its closing marker  */
    /* (counting the line breaks inside it).                                  */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None (the current position is at "//" or "/*").                    */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : false for an unterminated block comment.                    */
    /*------------------------------------------------------------------------*/
    bool Vjp_SkipComment()
    {
        int iEnd;                                        // end of the comment
        int i;

        if (Vjp_s[Vjp_i + 1] == '/')
        {   /* Line comment: to the end of the line. */
            iEnd = Vjp_s.IndexOf('\n', Vjp_i + 2, Vjp_n - Vjp_i - 2);
            if (iEnd < 0)
            {   /* The last line: to the end of the text. */
                iEnd = Vjp_n;
            }
            Vjp_i = iEnd;
            return(true);
        }

        /* Block comment: through its closing marker. */
        iEnd = Vjp_s.IndexOf("*/", Vjp_i + 2, Vjp_n - Vjp_i - 2,
            StringComparison.Ordinal);
        if (iEnd < 0)
        {   /* Never closed. */
            return(Vjp_Fail(Vjp_i, "This comment is never closed (no \"*/\")."));
        }

        for (i = Vjp_i; i < iEnd; i++)
        {
            if (Vjp_s[i] == '\n')
            {   /* A line break inside the comment. */
                Vjp_iNewlines++;
            }
        }
        Vjp_i = iEnd + 2;

        /* Past the comment. */
        return(true);
    }

    /*------------------------------------------------------------------------*/
    /* Vjp_SkipWhitespace:                                                    */
    /*                                                                        */
    /* Passes whitespace (not comments), counting line feeds.                 */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the current position is at the next non-whitespace or the   */
    /*            end.                                                        */
    /*------------------------------------------------------------------------*/
    void Vjp_SkipWhitespace()
    {
        char c;                                          // character being passed

        while (Vjp_i < Vjp_n)
        {
            c = Vjp_s[Vjp_i];
            if (c == '\n')
            {   /* A line break: counted. */
                Vjp_iNewlines++;
            }
            else if (c != ' ' && c != '\t' && c != '\r')
            {   /* Not whitespace: stop here. */
                return;
            }
            Vjp_i++;
        }
    }

    /*------------------------------------------------------------------------*/
    /* Vjp_SkipTrivia:                                                        */
    /*                                                                        */
    /* Passes whitespace and comments.                                        */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : false for an unterminated block comment.                    */
    /*------------------------------------------------------------------------*/
    bool Vjp_SkipTrivia()
    {
        while (true)
        {
            Vjp_SkipWhitespace();
            if (!Vjp_IsCommentStart(Vjp_i))
            {   /* Neither whitespace nor a comment: done. */
                return(true);
            }

            if (!Vjp_SkipComment())
            {   /* An unterminated comment. */
                return(false);
            }
        }
    }

    /*------------------------------------------------------------------------*/
    /* Vjp_TakeSameLineComments:                                              */
    /*                                                                        */
    /* Takes the comments that start on the current line: spaces and tabs,    */
    /* then a comment, then any further comments separated from it by spaces  */
    /* and tabs only (so they start on the line where the previous one        */
    /* ended).  When no comment follows on this line, nothing is taken and    */
    /* the position does not move: the spaces belong to whatever comes next.  */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sGap     : set to the spaces before the first comment, or null.    */
    /*     sComment : set to the comments (from the first one's start to the  */
    /*                last one's end), or null.                               */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : false for an unterminated block comment.                    */
    /*------------------------------------------------------------------------*/
    bool Vjp_TakeSameLineComments(out string sGap, out string sComment)
    {
        int iGapStart;                                   // where the spaces start
        int iStart;                                      // where the first comment starts
        int iEnd;                                        // where the last comment ends
        int j;

        sGap = null;
        sComment = null;
        iGapStart = Vjp_i;

        j = Vjp_i;
        while (j < Vjp_n && (Vjp_s[j] == ' ' || Vjp_s[j] == '\t'))
        {
            j++;
        }

        if (!Vjp_IsCommentStart(j))
        {   /* No comment on this line: leave the spaces for what follows. */
            return(true);
        }

        /* The first comment, then any that follow on the same line. */
        iStart = j;
        Vjp_i = j;
        while (true)
        {
            if (!Vjp_SkipComment())
            {   /* Unterminated. */
                return(false);
            }
            iEnd = Vjp_i;

            j = Vjp_i;
            while (j < Vjp_n && (Vjp_s[j] == ' ' || Vjp_s[j] == '\t'))
            {
                j++;
            }

            if (!Vjp_IsCommentStart(j))
            {   /* No further comment on this line. */
                break;
            }
            Vjp_i = j;
        }

        Vjp_i = iEnd;
        sGap = Vjp_Intern(iGapStart, iStart);
        sComment = Vjp_s.Substring(iStart, iEnd - iStart);

        /* The comments, and the position just after them. */
        return(true);
    }

    /*------------------------------------------------------------------------*/
    /* Vjp_ParseItems:                                                        */
    /*                                                                        */
    /* Reads a container's items, from just after its opening bracket (or     */
    /* the start of the text for the document) through its closing bracket    */
    /* (or the end of the text), following the attribution rules in the file  */
    /* header.  Checks: commas between values and nowhere else (no trailing   */
    /* comma), the right closing bracket, keys on object members, and one     */
    /* value in the document.                                                 */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     container : the container, empty, to fill.                         */
    /*     cClose    : its closing bracket; '\0' for the document.            */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : false on an error.                                          */
    /*------------------------------------------------------------------------*/
    public bool Vjp_ParseItems(VoorheesNode container, char cClose)
    {
        VoorheesItem item;                               // the item being read
        bool bDocument;                                  // reading the document rather than a container
        bool bObject;                                    // reading an object: items are members
        bool bAfterComma;                                // a comma was just read: a value must follow
        bool bValueDone;                                 // a value was read without a comma: only the end may follow
        int iLeadStart;                                  // where the next item's lead starts
        int iCommentStart;                               // where a comment item starts
        int iGapStart;                                   // where a gap starts
        int iNewlinesSaved;                              // line count before looking past a value
        int iCommaPos;                                   // where the last comma was
        string sGap;                                     // spaces before same-line comments
        string sComment;                                 // same-line comments
        char c;                                          // character at the current position

        bDocument = (container.Vn_iKind == VoorheesNode.VN_DOCUMENT);
        bObject = (container.Vn_iKind == VoorheesJson.VJS_OBJECT);
        bAfterComma = false;
        bValueDone = false;
        iCommaPos = -1;
        iLeadStart = Vjp_i;

        while (true)
        {
            /* Whitespace, then any comments that start on a line of their   */
            /* own: each is a comment item, its lead the whitespace before   */
            /* it.                                                           */
            while (true)
            {
                Vjp_SkipWhitespace();
                if (!Vjp_IsCommentStart(Vjp_i))
                {   /* Not a comment: an item, the end, or an error. */
                    break;
                }

                item = new VoorheesItem();
                item.Vi_iKind = VoorheesItem.VI_COMMENT;
                item.Vi_sLead = Vjp_Intern(iLeadStart, Vjp_i);
                iCommentStart = Vjp_i;
                if (!Vjp_TakeSameLineComments(out sGap, out sComment))
                {   /* Unterminated comment. */
                    return(false);
                }
                item.Vi_sComment = Vjp_s.Substring(iCommentStart,
                    Vjp_i - iCommentStart);
                item.Vi_bOddComments = VoorheesJson.Vjs_ComputeOdd(item);
                container.Vn_lstItems.Add(item);
                iLeadStart = Vjp_i;
            }

            /* The end of the container (or of the document)? */
            if (bDocument)
            {   /* The document ends at the end of the text. */
                if (Vjp_i >= Vjp_n)
                {   /* The end: there must have been a value. */
                    if (!bValueDone && container.Vn_Count() == 0)
                    {   /* Nothing at all. */
                        return(Vjp_Fail(Vjp_i, "The file is empty."));
                    }

                    if (!bValueDone)
                    {   /* Only comments. */
                        return(Vjp_Fail(Vjp_i,
                            "The file holds only comments, no JSON value."));
                    }

                    container.Vn_sClose = Vjp_Intern(iLeadStart, Vjp_i);
                    return(true);
                }

                if (bValueDone)
                {   /* Something after the top value. */
                    return(Vjp_Fail(Vjp_i, "Unexpected text after the JSON "
                        + "value: a file holds exactly one value."));
                }
            }
            else
            {   /* A container ends at its closing bracket. */
                if (Vjp_i >= Vjp_n)
                {   /* Ran out of text first. */
                    return(Vjp_Fail(Vjp_i, "The text ends before this "
                        + Vjp_ContainerName(cClose) + " is closed (missing \""
                        + cClose + "\")."));
                }

                c = Vjp_s[Vjp_i];
                if (c == cClose)
                {   /* The closing bracket. */
                    if (bAfterComma)
                    {   /* A comma with nothing after it. */
                        return(Vjp_Fail(iCommaPos, "Trailing comma: a value "
                            + "must follow a comma (JSON does not allow one "
                            + "before \"" + cClose + "\")."));
                    }

                    container.Vn_sClose = Vjp_Intern(iLeadStart, Vjp_i);
                    Vjp_i++;
                    return(true);
                }

                if (c == ']' || c == '}')
                {   /* The other kind of closing bracket. */
                    return(Vjp_Fail(Vjp_i, "Mismatched bracket: expected \""
                        + cClose + "\" to close the " + Vjp_ContainerName(cClose)
                        + ", found \"" + c + "\"."));
                }

                if (bValueDone)
                {   /* Another value without a comma before it. */
                    return(Vjp_Fail(Vjp_i, "Expected \",\" or \"" + cClose
                        + "\"."));
                }
            }

            /* An item: its lead is the whitespace before it. */
            item = new VoorheesItem();
            item.Vi_sLead = Vjp_Intern(iLeadStart, Vjp_i);
            bAfterComma = false;

            if (bObject)
            {   /* A member: key, colon, value. */
                item.Vi_iKind = VoorheesItem.VI_MEMBER;
                if (!Vjp_ParseKey(item))
                {   /* No valid key, colon or gap. */
                    return(false);
                }
            }
            else
            {   /* An element: just the value. */
                item.Vi_iKind = VoorheesItem.VI_ELEMENT;
            }

            item.Vi_node = Vjp_ParseValue(item);
            if (item.Vi_node == null)
            {   /* An invalid value. */
                return(false);
            }

            /* After the value: a comma, or the last value. */
            iGapStart = Vjp_i;
            iNewlinesSaved = Vjp_iNewlines;
            if (!Vjp_SkipTrivia())
            {   /* Unterminated comment. */
                return(false);
            }

            if (!bDocument && Vjp_i < Vjp_n && Vjp_s[Vjp_i] == ',')
            {   /* A comma: what came before it is the value gap, and   */
                /* comments after it on the same line belong to this    */
                /* item's line.                                         */
                item.Vi_sValueGap = Vjp_Intern(iGapStart, Vjp_i);
                iCommaPos = Vjp_i;
                Vjp_i++;
                bAfterComma = true;
                if (!Vjp_TakeSameLineComments(out sGap, out sComment))
                {   /* Unterminated comment. */
                    return(false);
                }
                item.Vi_sEolGap = sGap;
                item.Vi_sEolComment = sComment;
            }
            else
            {   /* No comma: the last value.  Look again from just after   */
                /* it: a same-line comment is its end-of-line comment;     */
                /* the rest is read as comment items and closing text.     */
                Vjp_i = iGapStart;
                Vjp_iNewlines = iNewlinesSaved;
                item.Vi_sValueGap = "";
                if (!Vjp_TakeSameLineComments(out sGap, out sComment))
                {   /* Unterminated comment. */
                    return(false);
                }
                item.Vi_sEolGap = sGap;
                item.Vi_sEolComment = sComment;
                bValueDone = true;
            }

            item.Vi_bOddComments = VoorheesJson.Vjs_ComputeOdd(item);
            container.Vn_lstItems.Add(item);
            iLeadStart = Vjp_i;
        }
    }

    /*------------------------------------------------------------------------*/
    /* Vjp_ContainerName:                                                     */
    /*                                                                        */
    /* "object" or "array", from a closing bracket, for messages.             */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     cClose : '}' or ']'.                                               */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the container's name.                                     */
    /*------------------------------------------------------------------------*/
    static string Vjp_ContainerName(char cClose)
    {
        if (cClose == '}')
        {   /* Braces close an object. */
            return("object");
        }

        /* Square brackets close an array. */
        return("array");
    }

    /*------------------------------------------------------------------------*/
    /* Vjp_ParseKey:                                                          */
    /*                                                                        */
    /* Reads a member's key and colon into an item: the key string, the key   */
    /* gap, the colon, the colon gap.  Leaves the position at the value.      */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     item : the member being read.                                      */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : false on an error.                                          */
    /*------------------------------------------------------------------------*/
    bool Vjp_ParseKey(VoorheesItem item)
    {
        string sText;                                    // the decoded key
        string sRaw;                                     // the key token, when it used escapes
        int iGapStart;                                   // where a gap starts

        if (Vjp_i >= Vjp_n || Vjp_s[Vjp_i] != '"')
        {   /* Members start with a key. */
            return(Vjp_Fail(Vjp_i, "Expected a key (a string in double "
                + "quotes)."));
        }

        if (!Vjp_ParseString(out sText, out sRaw))
        {   /* A bad string. */
            return(false);
        }
        item.Vi_sKey = Vjp_InternString(sText);
        if (sRaw != null)
        {   /* Written with escapes: keep them. */
            item.Vi_sKeyRaw = Vjp_InternString(sRaw);
        }

        iGapStart = Vjp_i;
        if (!Vjp_SkipTrivia())
        {   /* Unterminated comment. */
            return(false);
        }
        item.Vi_sKeyGap = Vjp_Intern(iGapStart, Vjp_i);

        if (Vjp_i >= Vjp_n || Vjp_s[Vjp_i] != ':')
        {   /* The colon is required. */
            return(Vjp_Fail(Vjp_i, "Expected \":\" after the key."));
        }
        Vjp_i++;

        iGapStart = Vjp_i;
        if (!Vjp_SkipTrivia())
        {   /* Unterminated comment. */
            return(false);
        }
        item.Vi_sColonGap = Vjp_Intern(iGapStart, Vjp_i);

        /* At the value. */
        return(true);
    }

    /*------------------------------------------------------------------------*/
    /* Vjp_ParseValue:                                                        */
    /*                                                                        */
    /* Reads one value at the current position: an object, an array, a        */
    /* string, a number, or true, false or null.  For a container, comments   */
    /* on the same line as its opening bracket go to the item that holds it.  */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     owner : the item that will hold the value.                         */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesNode : the value, or null on an error.                     */
    /*------------------------------------------------------------------------*/
    VoorheesNode Vjp_ParseValue(VoorheesItem owner)
    {
        VoorheesNode node;                               // the value being read
        string sText;                                    // a string's decoded text
        string sRaw;                                     // a string's token, when it used escapes
        string sGap;                                     // spaces before an open comment
        string sComment;                                 // the open comment
        int iNewlinesBefore;                             // line count at the opening bracket
        char c;                                          // first character of the value
        char cClose;                                     // a container's closing bracket

        if (Vjp_i >= Vjp_n)
        {   /* Nothing where a value should be. */
            Vjp_Fail(Vjp_i, "The text ends where a value was expected.");
            return(null);
        }

        c = Vjp_s[Vjp_i];

        if (c == '{' || c == '[')
        {   /* An object or array. */
            if (Vjp_iDepth >= VoorheesJson.VJS_MAXDEPTH)
            {   /* Deeper than the reader will go. */
                Vjp_Fail(Vjp_i, "Nested too deeply (more than "
                    + VoorheesJson.VJS_MAXDEPTH.ToString(CultureInfo.InvariantCulture)
                    + " levels).");
                return(null);
            }

            if (c == '{')
            {   /* Braces: an object, closed by a brace. */
                node = VoorheesNode.Vn_NewContainer(VoorheesJson.VJS_OBJECT);
                cClose = '}';
            }
            else
            {   /* Square brackets: an array, closed by a square bracket. */
                node = VoorheesNode.Vn_NewContainer(VoorheesJson.VJS_ARRAY);
                cClose = ']';
            }

            iNewlinesBefore = Vjp_iNewlines;
            Vjp_i++;
            Vjp_iDepth++;

            /* Comments on the bracket's line belong to the holding item. */
            if (!Vjp_TakeSameLineComments(out sGap, out sComment))
            {   /* Unterminated comment. */
                return(null);
            }
            owner.Vi_sOpenGap = sGap;
            owner.Vi_sOpenComment = sComment;

            if (!Vjp_ParseItems(node, cClose))
            {   /* An error inside. */
                return(null);
            }

            Vjp_iDepth--;
            node.Vn_bOneLine = (Vjp_iNewlines == iNewlinesBefore);

            /* The container. */
            return(node);
        }

        if (c == '"')
        {   /* A string. */
            if (!Vjp_ParseString(out sText, out sRaw))
            {   /* A bad string. */
                return(null);
            }

            node = VoorheesNode.Vn_NewLeaf(VoorheesJson.VJS_STRING, sText);
            node.Vn_sRaw = sRaw;

            /* The string, its token kept when it used escapes. */
            return(node);
        }

        if (c == '-' || (c >= '0' && c <= '9'))
        {   /* A number. */
            return(Vjp_ParseNumber());
        }

        if (c == 't' || c == 'f' || c == 'n')
        {   /* true, false or null. */
            return(Vjp_ParseLiteral());
        }

        if (c == '/')
        {   /* A slash that does not start a comment. */
            Vjp_Fail(Vjp_i, "Unexpected \"/\" (comments start with // or /*).");
            return(null);
        }

        if (c == ']' || c == '}' || c == ',')
        {   /* Punctuation where a value should be. */
            Vjp_Fail(Vjp_i, "Expected a value, found \"" + c + "\".");
            return(null);
        }

        /* Anything else cannot start a JSON value. */
        Vjp_Fail(Vjp_i, "Unexpected character \"" + c + "\": not the start "
            + "of a JSON value.");
        return(null);
    }

    /*------------------------------------------------------------------------*/
    /* Vjp_ParseString:                                                       */
    /*                                                                        */
    /* Reads a string token at the current position (on its opening quote).   */
    /* The usual case, a string without escapes, is one scan and one          */
    /* substring.  Escapes allowed: \" \\ \/ \b \f \n \r \t and \u with four  */
    /* hex digits.  Refused: any other escape, a raw control character        */
    /* (including a raw tab or line break), and a missing closing quote.      */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sText : set to the decoded string.                                 */
    /*     sRaw  : set to the token as written (quotes included) when it used */
    /*             any escape, else null -- the writer regenerates exactly    */
    /*             that token from sText.                                     */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : false on an error.                                          */
    /*------------------------------------------------------------------------*/
    bool Vjp_ParseString(out string sText, out string sRaw)
    {
        StringBuilder sb;                                // the decoded string, when it has escapes
        int iStart;                                      // the opening quote
        int j;                                           // scan position
        int iCode;                                       // a \u escape's value
        int k;
        char c;                                          // character being read
        char h;                                          // a hex digit

        sText = null;
        sRaw = null;
        iStart = Vjp_i;
        j = Vjp_i + 1;

        /* Fast scan: to the closing quote, unless an escape comes first. */
        while (j < Vjp_n)
        {
            c = Vjp_s[j];
            if (c == '"' || c == '\\')
            {   /* The end, or an escape: either way the fast scan stops. */
                break;
            }

            if (c < 0x20)
            {   /* A raw control character. */
                return(Vjp_Fail(j, "A string can't contain a raw line break, "
                    + "tab or other control character; write it as an "
                    + "escape such as \\n or \\t."));
            }
            j++;
        }

        if (j >= Vjp_n)
        {   /* No closing quote. */
            return(Vjp_Fail(iStart, "This string is never closed (no closing "
                + "\")."));
        }

        if (Vjp_s[j] == '"')
        {   /* No escapes: the text is what is between the quotes. */
            sText = Vjp_s.Substring(iStart + 1, j - iStart - 1);
            Vjp_i = j + 1;
            return(true);
        }

        /* Escapes: decode from here on. */
        sb = new StringBuilder(Vjp_s, iStart + 1, j - iStart - 1, (j - iStart) + 16);
        while (true)
        {
            if (j >= Vjp_n)
            {   /* No closing quote. */
                return(Vjp_Fail(iStart, "This string is never closed (no "
                    + "closing \")."));
            }

            c = Vjp_s[j];
            if (c == '"')
            {   /* The closing quote. */
                break;
            }

            if (c < 0x20)
            {   /* A raw control character. */
                return(Vjp_Fail(j, "A string can't contain a raw line break, "
                    + "tab or other control character; write it as an "
                    + "escape such as \\n or \\t."));
            }

            if (c != '\\')
            {   /* An ordinary character. */
                sb.Append(c);
                j++;
                continue;
            }

            /* An escape. */
            if (j + 1 >= Vjp_n)
            {   /* A backslash at the very end. */
                return(Vjp_Fail(j, "This string is never closed (no closing "
                    + "\")."));
            }

            switch (Vjp_s[j + 1])
            {
                case '"':  sb.Append('"');  j += 2; break;   // quote
                case '\\': sb.Append('\\'); j += 2; break;   // backslash
                case '/':  sb.Append('/');  j += 2; break;   // slash
                case 'b':  sb.Append('\b'); j += 2; break;   // backspace
                case 'f':  sb.Append('\f'); j += 2; break;   // form feed
                case 'n':  sb.Append('\n'); j += 2; break;   // line feed
                case 'r':  sb.Append('\r'); j += 2; break;   // carriage return
                case 't':  sb.Append('\t'); j += 2; break;   // tab

                case 'u':
                    /* Four hex digits: a UTF-16 code unit. */
                    if (j + 6 > Vjp_n)
                    {   /* Too short. */
                        return(Vjp_Fail(j, "Bad \\u escape: it needs four hex "
                            + "digits."));
                    }

                    iCode = 0;
                    for (k = j + 2; k < j + 6; k++)
                    {
                        h = Vjp_s[k];
                        if (h >= '0' && h <= '9')
                        {   /* A decimal digit. */
                            iCode = iCode * 16 + (h - '0');
                        }
                        else if (h >= 'a' && h <= 'f')
                        {   /* A lower-case hex letter. */
                            iCode = iCode * 16 + (h - 'a' + 10);
                        }
                        else if (h >= 'A' && h <= 'F')
                        {   /* An upper-case hex letter. */
                            iCode = iCode * 16 + (h - 'A' + 10);
                        }
                        else
                        {   /* Not hex. */
                            return(Vjp_Fail(j, "Bad \\u escape: it needs four "
                                + "hex digits."));
                        }
                    }
                    sb.Append((char)iCode);
                    j += 6;
                    break;

                default:
                    /* Anything else after a backslash. */
                    return(Vjp_Fail(j, "Invalid escape \"\\" + Vjp_s[j + 1]
                        + "\" in a string (allowed: \\\" \\\\ \\/ \\b \\f \\n "
                        + "\\r \\t \\uXXXX)."));
            }
        }

        sText = sb.ToString();
        sRaw = Vjp_s.Substring(iStart, j + 1 - iStart);
        Vjp_i = j + 1;

        /* Decoded, with the token kept. */
        return(true);
    }

    /*------------------------------------------------------------------------*/
    /* Vjp_IsDigit:                                                           */
    /*                                                                        */
    /* Whether the character at a position is 0-9.                            */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iPos : the position (may be past the end).                         */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true for a digit inside the text.                           */
    /*------------------------------------------------------------------------*/
    bool Vjp_IsDigit(int iPos)
    {
        /* Inside the text and an ASCII digit. */
        return(iPos < Vjp_n && Vjp_s[iPos] >= '0' && Vjp_s[iPos] <= '9');
    }

    /*------------------------------------------------------------------------*/
    /* Vjp_IsWordChar:                                                        */
    /*                                                                        */
    /* Whether the character at a position would continue a number or a       */
    /* literal (a letter, digit, underscore or decimal point): one may not    */
    /* follow directly, or "01", "1.", "truex" would be read as something     */
    /* shorter.                                                               */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iPos : the position (may be past the end).                         */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true when the token would run on.                           */
    /*------------------------------------------------------------------------*/
    bool Vjp_IsWordChar(int iPos)
    {
        char c;                                          // the character

        if (iPos >= Vjp_n)
        {   /* The end: nothing runs on. */
            return(false);
        }

        c = Vjp_s[iPos];

        /* Letters, digits, underscore, decimal point. */
        return(char.IsLetterOrDigit(c) || c == '_' || c == '.');
    }

    /*------------------------------------------------------------------------*/
    /* Vjp_ParseNumber:                                                       */
    /*                                                                        */
    /* Reads a number token by JSON's grammar: optional minus; 0 or a digit   */
    /* 1-9 followed by digits; optionally a point and at least one digit;     */
    /* optionally e or E, an optional sign, and at least one digit.  The      */
    /* number is kept as text, exactly as written.                            */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None (the current position is at "-" or a digit).                  */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesNode : the number, or null on an error.                    */
    /*------------------------------------------------------------------------*/
    VoorheesNode Vjp_ParseNumber()
    {
        int iStart;                                      // first character of the number

        iStart = Vjp_i;
        if (Vjp_s[Vjp_i] == '-')
        {   /* A minus sign. */
            Vjp_i++;
        }

        if (!Vjp_IsDigit(Vjp_i))
        {   /* A minus with no digits. */
            Vjp_Fail(iStart, "Invalid number: a digit must follow \"-\".");
            return(null);
        }

        if (Vjp_s[Vjp_i] == '0')
        {   /* Zero: no more integer digits allowed. */
            Vjp_i++;
            if (Vjp_IsDigit(Vjp_i))
            {   /* "01" and the like. */
                Vjp_Fail(iStart, "Invalid number: JSON numbers can't have "
                    + "leading zeros.");
                return(null);
            }
        }
        else
        {   /* 1-9, then any digits. */
            while (Vjp_IsDigit(Vjp_i))
            {
                Vjp_i++;
            }
        }

        if (Vjp_i < Vjp_n && Vjp_s[Vjp_i] == '.')
        {   /* A fraction: digits must follow the point. */
            Vjp_i++;
            if (!Vjp_IsDigit(Vjp_i))
            {   /* "1." and the like. */
                Vjp_Fail(iStart, "Invalid number: a digit must follow the "
                    + "decimal point.");
                return(null);
            }

            while (Vjp_IsDigit(Vjp_i))
            {
                Vjp_i++;
            }
        }

        if (Vjp_i < Vjp_n && (Vjp_s[Vjp_i] == 'e' || Vjp_s[Vjp_i] == 'E'))
        {   /* An exponent: optional sign, then digits. */
            Vjp_i++;
            if (Vjp_i < Vjp_n && (Vjp_s[Vjp_i] == '+' || Vjp_s[Vjp_i] == '-'))
            {   /* Its sign. */
                Vjp_i++;
            }

            if (!Vjp_IsDigit(Vjp_i))
            {   /* "1e" and the like. */
                Vjp_Fail(iStart, "Invalid number: a digit must follow the "
                    + "exponent.");
                return(null);
            }

            while (Vjp_IsDigit(Vjp_i))
            {
                Vjp_i++;
            }
        }

        if (Vjp_IsWordChar(Vjp_i))
        {   /* Something runs straight on from the number. */
            Vjp_Fail(iStart, "Invalid number.");
            return(null);
        }

        /* The number, as written. */
        return(VoorheesNode.Vn_NewLeaf(VoorheesJson.VJS_NUMBER,
            Vjp_s.Substring(iStart, Vjp_i - iStart)));
    }

    /*------------------------------------------------------------------------*/
    /* Vjp_ParseLiteral:                                                      */
    /*                                                                        */
    /* Reads true, false or null, spelt exactly so (lower case) and not       */
    /* running on into further letters.                                       */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None (the current position is at t, f or n).                       */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesNode : the value, or null on an error.                     */
    /*------------------------------------------------------------------------*/
    VoorheesNode Vjp_ParseLiteral()
    {
        string[] arrWords;                               // the three literals
        int[] arrKinds;                                  // their node kinds
        int w;

        arrWords = new string[] { "true", "false", "null" };
        arrKinds = new int[] { VoorheesJson.VJS_BOOLEAN, VoorheesJson.VJS_BOOLEAN,
            VoorheesJson.VJS_NULL };

        for (w = 0; w < arrWords.Length; w++)
        {
            if (Vjp_i + arrWords[w].Length <= Vjp_n
                && string.CompareOrdinal(Vjp_s, Vjp_i, arrWords[w], 0,
                    arrWords[w].Length) == 0
                && !Vjp_IsWordChar(Vjp_i + arrWords[w].Length))
            {   /* This literal, standing alone. */
                Vjp_i += arrWords[w].Length;
                return(VoorheesNode.Vn_NewLeaf(arrKinds[w], arrWords[w]));
            }
        }

        /* Close, but not one of them. */
        Vjp_Fail(Vjp_i, "Invalid literal: expected true, false or null.");
        return(null);
    }

    /*------------------------------------------------------------------------*/
    /* Vjp_ParseSnippet:                                                      */
    /*                                                                        */
    /* Reads a Raw edit (Vjs_ApplyRawEdit) as one item of a given kind, the   */
    /* whole text and nothing else:                                           */
    /*     VI_MEMBER  : key, colon, value, then optional comments;            */
    /*     VI_ELEMENT : value, then optional comments;                        */
    /*     VI_COMMENT : one or more comments only.                            */
    /* Comments after a value that is written over several lines (a           */
    /* multi-line container) and on the line of its closing bracket become    */
    /* its end-of-line comment, as Vjs_RenderItem shows them; after anything  */
    /* else they stay between the value and its comma, as typed.              */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iKind : the VI_ kind the item must be.                             */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesItem : the item (lead left null), or null on an error.     */
    /*------------------------------------------------------------------------*/
    public VoorheesItem Vjp_ParseSnippet(int iKind)
    {
        VoorheesItem item;                               // the item being read
        int iGapStart;                                   // where the text after the value starts
        int iNewlinesSaved;                              // line count at that point
        string sGap;                                     // spaces before same-line comments
        string sComment;                                 // same-line comments

        item = new VoorheesItem();
        item.Vi_iKind = iKind;

        /* Leading whitespace is not significant. */
        Vjp_SkipWhitespace();

        if (iKind == VoorheesItem.VI_COMMENT)
        {   /* Comments only. */
            if (!Vjp_IsCommentStart(Vjp_i))
            {   /* Something that is not a comment. */
                Vjp_Fail(Vjp_i, "A comment entry can hold only comments "
                    + "(starting with // or /*).");
                return(null);
            }

            iGapStart = Vjp_i;
            if (!Vjp_SkipTrivia())
            {   /* Unterminated comment. */
                return(null);
            }

            if (Vjp_i < Vjp_n)
            {   /* Something after the comments. */
                Vjp_Fail(Vjp_i, "A comment entry can hold only comments.");
                return(null);
            }

            item.Vi_sComment = Vjp_s.Substring(iGapStart, Vjp_n - iGapStart);
            return(item);
        }

        if (iKind == VoorheesItem.VI_MEMBER)
        {   /* A member: its key first. */
            if (Vjp_IsCommentStart(Vjp_i))
            {   /* A comment before the key has no place in the item. */
                Vjp_Fail(Vjp_i, "A comment before the key belongs on a line "
                    + "of its own: add it as a separate comment entry.");
                return(null);
            }

            if (!Vjp_ParseKey(item))
            {   /* No valid key or colon. */
                return(null);
            }
        }
        else if (Vjp_IsCommentStart(Vjp_i))
        {   /* A comment before the value has no place in the item. */
            Vjp_Fail(Vjp_i, "A comment before the value belongs on a line of "
                + "its own: add it as a separate comment entry.");
            return(null);
        }

        item.Vi_node = Vjp_ParseValue(item);
        if (item.Vi_node == null)
        {   /* An invalid value. */
            return(null);
        }

        /* After the value only comments may follow. */
        iGapStart = Vjp_i;
        iNewlinesSaved = Vjp_iNewlines;
        if (!Vjp_SkipTrivia())
        {   /* Unterminated comment. */
            return(null);
        }

        if (Vjp_i < Vjp_n)
        {   /* Something more than one item. */
            if (Vjp_s[Vjp_i] == ',')
            {   /* A comma: Voorhees writes those itself. */
                Vjp_Fail(Vjp_i, "Leave out the comma: Voorhees writes the "
                    + "commas between entries.");
            }
            else
            {   /* Anything else. */
                Vjp_Fail(Vjp_i, "Unexpected text after the value: the Raw "
                    + "box holds exactly one entry.");
            }
            return(null);
        }

        if (item.Vi_node.Vn_IsContainer() && !item.Vi_node.Vn_bOneLine)
        {   /* A multi-line container: same-line comments after its closing    */
            /* bracket are its end-of-line comment; nothing may follow them.   */
            Vjp_i = iGapStart;
            Vjp_iNewlines = iNewlinesSaved;
            item.Vi_sValueGap = "";
            if (!Vjp_TakeSameLineComments(out sGap, out sComment))
            {   /* Unterminated comment. */
                return(null);
            }
            item.Vi_sEolGap = sGap;
            item.Vi_sEolComment = sComment;

            if (!Vjp_SkipTrivia())
            {   /* Unterminated comment. */
                return(null);
            }

            if (Vjp_i < Vjp_n)
            {   /* Comments on later lines. */
                Vjp_Fail(Vjp_i, "A comment on a later line belongs on a line "
                    + "of its own: add it as a separate comment entry.");
                return(null);
            }
        }
        else
        {   /* Anything else: whatever follows the value stays as typed. */
            item.Vi_sValueGap = Vjp_s.Substring(iGapStart, Vjp_n - iGapStart);
        }

        /* The item. */
        return(item);
    }
}

/*----------------------------------------------------------------------------*/
/* VoorheesJsonWriter                                                         */
/*                                                                            */
/* One write into one StringBuilder.  Explicit text is appended exactly as    */
/* stored; null fields are generated from the document's style.               */
/*                                                                            */
/* Generated layout needs the indentation of the line a container starts      */
/* on.  The writer tracks where the current output line starts                */
/* (Vjw_iLineStart) as text is appended, and reads a line's indentation back  */
/* only when something has to be generated, so an unedited document costs     */
/* nothing extra.                                                             */
/*                                                                            */
/* Safety: after text that ends inside a // comment, whatever comes next must */
/* start on a new line or the comment would swallow it.  Explicit text from a */
/* file never needs this, but an edit can (a comment added at the end of a    */
/* line that goes on, a Raw edit ending in a comment before the comma), so    */
/* Vjw_PutTrivia notes it and Vjw_PutText starts a new line, at the current   */
/* line's indentation, before anything that does not already.  The output     */
/* therefore always reads back as valid.                                      */
/*----------------------------------------------------------------------------*/
class VoorheesJsonWriter
{
    StringBuilder Vjw_sb;                                // the output
    VoorheesDocument Vjw_doc;                            // the document, for its style
    int Vjw_iLineStart;                                  // where the current output line starts
    bool Vjw_bNeedBreak;                                 // the last text ended inside a // comment
    int Vjw_iLimit;                                      // stop writing once the output is this long (int.MaxValue: never)
    bool Vjw_bStopped;                                   // the limit was reached; the output is cut short

    /*------------------------------------------------------------------------*/
    /* Vjw_Create:                                                            */
    /*                                                                        */
    /* A writer for a document.  Output may start with a prefix, which is     */
    /* taken as the start of the first line: Vjs_RenderItem passes the item's */
    /* line indentation so generated lines inside it are indented as in the   */
    /* file, then drops the prefix from the result.                           */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc     : the document (its style).                                */
    /*     sPrefix : text the first line starts with ("" for a whole file).   */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesJsonWriter : ready to write.                               */
    /*------------------------------------------------------------------------*/
    public static VoorheesJsonWriter Vjw_Create(VoorheesDocument doc,
        string sPrefix)
    {
        VoorheesJsonWriter writer;                       // the new writer

        writer = new VoorheesJsonWriter();
        writer.Vjw_doc = doc;
        writer.Vjw_sb = new StringBuilder(4096);
        writer.Vjw_sb.Append(sPrefix);
        writer.Vjw_iLineStart = 0;
        writer.Vjw_bNeedBreak = false;
        writer.Vjw_iLimit = int.MaxValue;
        writer.Vjw_bStopped = false;

        /* Ready. */
        return(writer);
    }

    /*------------------------------------------------------------------------*/
    /* Vjw_SetLimit:                                                          */
    /*                                                                        */
    /* Makes the writer stop once its output reaches a length: further text   */
    /* is dropped and containers stop at the next item.  For showing the      */
    /* start of a huge item without writing all of it.                        */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iLimit : output length (prefix included) at which to stop.         */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the limit is set.                                           */
    /*------------------------------------------------------------------------*/
    public void Vjw_SetLimit(int iLimit)
    {
        Vjw_iLimit = iLimit;
    }

    /*------------------------------------------------------------------------*/
    /* Vjw_Stopped:                                                           */
    /*                                                                        */
    /* Whether the output went past the limit, so the result is incomplete.   */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true when the item's text is longer than the limit.         */
    /*------------------------------------------------------------------------*/
    public bool Vjw_Stopped()
    {
        /* Set by Vjw_Append. */
        return(Vjw_bStopped);
    }

    /*------------------------------------------------------------------------*/
    /* Vjw_ResultLimited:                                                     */
    /*                                                                        */
    /* The text written, from an offset, cut to a length.                     */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iFrom     : where to start (the prefix's length).                  */
    /*     iMaxChars : most characters wanted.                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the output.                                               */
    /*------------------------------------------------------------------------*/
    public string Vjw_ResultLimited(int iFrom, int iMaxChars)
    {
        /* Up to iMaxChars after the prefix. */
        return(Vjw_sb.ToString(iFrom, Math.Min(iMaxChars, Vjw_sb.Length - iFrom)));
    }

    /*------------------------------------------------------------------------*/
    /* Vjw_Result:                                                            */
    /*                                                                        */
    /* The text written, from a given offset.                                 */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iFrom : where to start (the prefix's length, to drop it).          */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the output.                                               */
    /*------------------------------------------------------------------------*/
    public string Vjw_Result(int iFrom)
    {
        /* Everything after the prefix. */
        return(Vjw_sb.ToString(iFrom, Vjw_sb.Length - iFrom));
    }

    /*------------------------------------------------------------------------*/
    /* Vjw_Append:                                                            */
    /*                                                                        */
    /* Appends text and moves the line start past its last line break.  With  */
    /* a limit, the output is CUT (Vjw_bStopped) as soon as it holds more     */
    /* than the limit -- whether this text ran past it, or more text arrives  */
    /* once it is reached (then dropped).  Reaching the limit exactly is not  */
    /* a cut: nothing may follow.  (Noting it only for text arriving after    */
    /* the limit missed the case where the LAST piece of text ran past it:    */
    /* the result was cut by Vjw_ResultLimited but reported complete.)        */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     s : the text (not empty).                                          */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the output grows.                                           */
    /*------------------------------------------------------------------------*/
    void Vjw_Append(string s)
    {
        int iBefore;                                     // output length before
        int iNewline;                                    // last line break in s

        if (Vjw_sb.Length >= Vjw_iLimit)
        {   /* Past the limit: drop the text and note that the output is cut. */
            Vjw_bStopped = true;
            return;
        }

        iBefore = Vjw_sb.Length;
        Vjw_sb.Append(s);
        iNewline = s.LastIndexOf('\n');
        if (iNewline >= 0)
        {   /* A new line starts after it. */
            Vjw_iLineStart = iBefore + iNewline + 1;
        }

        if (Vjw_sb.Length > Vjw_iLimit)
        {   /* This text ran past the limit: what is kept will be cut. */
            Vjw_bStopped = true;
        }
    }

    /*------------------------------------------------------------------------*/
    /* Vjw_PutText:                                                           */
    /*                                                                        */
    /* Writes text that cannot end inside a comment (a token, a bracket, a    */
    /* comma, whitespace).  If the text before it ended inside a // comment,  */
    /* a line break (and the current line's indentation) goes first unless    */
    /* the text starts with one itself.                                       */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     s : the text; null or empty writes nothing.                        */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the output grows.                                           */
    /*------------------------------------------------------------------------*/
    public void Vjw_PutText(string s)
    {
        string sIndent;                                  // the current line's indentation

        if (string.IsNullOrEmpty(s))
        {   /* Nothing to write (and nothing to protect yet). */
            return;
        }

        if (Vjw_bNeedBreak && s[0] != '\n' && s[0] != '\r')
        {   /* A // comment is still open: end its line first. */
            sIndent = Vjw_IndentAt(Vjw_iLineStart);
            Vjw_Append(Vjw_doc.Vd_sNewline + sIndent);
        }

        Vjw_bNeedBreak = false;
        Vjw_Append(s);
    }

    /*------------------------------------------------------------------------*/
    /* Vjw_PutTrivia:                                                         */
    /*                                                                        */
    /* Writes text that may hold comments (a gap or a comment), then notes    */
    /* whether it ended inside a // comment.                                  */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     s : the text; null or empty writes nothing.                        */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the output grows.                                           */
    /*------------------------------------------------------------------------*/
    public void Vjw_PutTrivia(string s)
    {
        if (string.IsNullOrEmpty(s))
        {   /* Nothing to write. */
            return;
        }

        Vjw_PutText(s);
        if (s.IndexOf('/') >= 0)
        {   /* It holds a comment: does one run to its end? */
            Vjw_bNeedBreak = VoorheesJson.Vjs_EndsInLineComment(s);
        }
    }

    /*------------------------------------------------------------------------*/
    /* Vjw_IndentAt:                                                          */
    /*                                                                        */
    /* The indentation (leading spaces and tabs) of the output line starting  */
    /* at a position.                                                         */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iLineStart : where the line starts in the output.                  */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : its indentation; "" for none.                             */
    /*------------------------------------------------------------------------*/
    string Vjw_IndentAt(int iLineStart)
    {
        int iEnd;                                        // end of the indentation
        char c;                                          // character being looked at

        iEnd = iLineStart;
        while (iEnd < Vjw_sb.Length)
        {
            c = Vjw_sb[iEnd];
            if (c != ' ' && c != '\t')
            {   /* The end of the indentation. */
                break;
            }
            iEnd++;
        }

        /* The spaces and tabs at the line's start. */
        return(Vjw_sb.ToString(iLineStart, iEnd - iLineStart));
    }

    /*------------------------------------------------------------------------*/
    /* Vjw_WriteContainer:                                                    */
    /*                                                                        */
    /* Writes a container (or the document): its opening bracket and the      */
    /* holding item's open comment, then each item (lead, content, comma      */
    /* after every value but the last, end-of-line comment), then its closing */
    /* text and bracket.  Generated pieces:                                   */
    /*     o lead of a value that is not the container's first value: a copy  */
    /*       of the lead of the first such value that has one, if any (new    */
    /*       items look like their neighbours);                               */
    /*     o otherwise, in a multi-line container: a line break, the          */
    /*       container's line indentation and one indentation unit; in a      */
    /*       one-line container: nothing before the first item, one space     */
    /*       before the others; in the document: nothing before the first     */
    /*       item, a line break before the others;                            */
    /*     o closing text: nothing for an empty or one-line container, a line */
    /*       break and the container's line indentation for a multi-line one, */
    /*       a line break at the end of the document.                         */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     node  : the container or document.                                 */
    /*     owner : the item holding it (for its open comment); null for the   */
    /*             document.                                                  */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the container is written.                                   */
    /*------------------------------------------------------------------------*/
    public void Vjw_WriteContainer(VoorheesNode node, VoorheesItem owner)
    {
        VoorheesItem item;                               // the item being written
        bool bDocument;                                  // writing the document: no brackets, no commas
        string sIndent;                                  // the container's line indentation, once needed
        string sSiblingLead;                             // a value's lead to copy, once looked for
        bool bSiblingLooked;                             // sSiblingLead has been looked for
        string sLead;                                    // the lead to write
        string sClose;                                   // the closing text to write
        int iOpenLineStart;                              // start of the line the container opens on
        int iLastValue;                                  // position of the last value item
        int iValueOrdinal;                               // values written so far
        int k;

        bDocument = (node.Vn_iKind == VoorheesNode.VN_DOCUMENT);
        iOpenLineStart = Vjw_iLineStart;
        sIndent = null;
        sSiblingLead = null;
        bSiblingLooked = false;

        if (!bDocument)
        {   /* The opening bracket, and any comment on its line. */
            if (node.Vn_iKind == VoorheesJson.VJS_OBJECT)
            {   /* An object. */
                Vjw_PutText("{");
            }
            else
            {   /* An array. */
                Vjw_PutText("[");
            }

            if (owner != null && owner.Vi_sOpenComment != null)
            {   /* A comment after the bracket. */
                Vjw_PutText(VoorheesJson.Vjs_GapOrDefault(owner.Vi_sOpenGap));
                Vjw_PutTrivia(owner.Vi_sOpenComment);
            }
        }

        iLastValue = node.Vn_LastValuePos();
        iValueOrdinal = 0;

        for (k = 0; k < node.Vn_lstItems.Count; k++)
        {
            if (Vjw_sb.Length >= Vjw_iLimit)
            {   /* Past the limit: stop here rather than walk the rest. */
                Vjw_bStopped = true;
                break;
            }

            item = node.Vn_lstItems[k];

            /* The lead: stored, or generated. */
            sLead = item.Vi_sLead;
            if (sLead == null)
            {   /* Generated: copy a neighbour's, or lay it out. */
                if (item.Vi_IsValue() && iValueOrdinal > 0 && !bSiblingLooked)
                {   /* First time a lead to copy is wanted: look for one. */
                    sSiblingLead = Vjw_FindSiblingLead(node);
                    bSiblingLooked = true;
                }

                if (item.Vi_IsValue() && iValueOrdinal > 0 && sSiblingLead != null)
                {   /* A later value: look like the other later values. */
                    sLead = sSiblingLead;
                }
                else if (bDocument)
                {   /* Top level: each item on its own line, at the margin. */
                    if (k == 0)
                    {   /* The first: at the very start. */
                        sLead = "";
                    }
                    else
                    {   /* The others: on the next line. */
                        sLead = Vjw_doc.Vd_sNewline;
                    }
                }
                else if (node.Vn_bOneLine)
                {   /* One-line container: space-separated. */
                    if (k == 0)
                    {   /* Straight after the bracket. */
                        sLead = "";
                    }
                    else
                    {   /* After the comma. */
                        sLead = " ";
                    }
                }
                else
                {   /* Multi-line container: one level deeper on a new line. */
                    if (sIndent == null)
                    {   /* Work out the container's indentation once. */
                        sIndent = Vjw_IndentAt(iOpenLineStart);
                    }
                    sLead = Vjw_doc.Vd_sNewline + sIndent + Vjw_doc.Vd_sIndentUnit;
                }
            }
            Vjw_PutText(sLead);

            Vjw_WriteItemContent(item);

            if (item.Vi_IsValue())
            {   /* A value: a comma unless it is the last one. */
                if (!bDocument && k != iLastValue)
                {   /* More values follow. */
                    Vjw_PutText(",");
                }
                iValueOrdinal++;
            }

            if (item.Vi_sEolComment != null)
            {   /* The comment at the end of its line. */
                Vjw_PutText(VoorheesJson.Vjs_GapOrDefault(item.Vi_sEolGap));
                Vjw_PutTrivia(item.Vi_sEolComment);
            }
        }

        /* The closing text: stored, or generated. */
        sClose = node.Vn_sClose;
        if (sClose == null)
        {   /* Generated. */
            if (bDocument)
            {   /* The file ends with a line break. */
                sClose = Vjw_doc.Vd_sNewline;
            }
            else if (node.Vn_lstItems.Count == 0 || node.Vn_bOneLine)
            {   /* Empty or one-line: the bracket follows at once. */
                sClose = "";
            }
            else
            {   /* Multi-line: the bracket on its own line, under the opening one's line. */
                if (sIndent == null)
                {   /* Work out the container's indentation. */
                    sIndent = Vjw_IndentAt(iOpenLineStart);
                }
                sClose = Vjw_doc.Vd_sNewline + sIndent;
            }
        }
        Vjw_PutText(sClose);

        if (!bDocument)
        {   /* The closing bracket. */
            if (node.Vn_iKind == VoorheesJson.VJS_OBJECT)
            {   /* An object. */
                Vjw_PutText("}");
            }
            else
            {   /* An array. */
                Vjw_PutText("]");
            }
        }
    }

    /*------------------------------------------------------------------------*/
    /* Vjw_FindSiblingLead:                                                   */
    /*                                                                        */
    /* The lead a generated value lead copies: that of the first value in the */
    /* container, after its first value, that has a stored lead.              */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     node : the container.                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the lead, or null when there is none to copy.             */
    /*------------------------------------------------------------------------*/
    static string Vjw_FindSiblingLead(VoorheesNode node)
    {
        bool bSeenFirst;                                 // the first value has been passed
        int k;

        bSeenFirst = false;
        for (k = 0; k < node.Vn_lstItems.Count; k++)
        {
            if (!node.Vn_lstItems[k].Vi_IsValue())
            {   /* Comments do not count. */
                continue;
            }

            if (!bSeenFirst)
            {   /* The first value's lead follows the bracket, not a comma: skip it. */
                bSeenFirst = true;
                continue;
            }

            if (node.Vn_lstItems[k].Vi_sLead != null)
            {   /* A later value with a stored lead. */
                return(node.Vn_lstItems[k].Vi_sLead);
            }
        }

        /* None to copy. */
        return(null);
    }

    /*------------------------------------------------------------------------*/
    /* Vjw_WriteItemContent:                                                  */
    /*                                                                        */
    /* Writes an item without its lead, comma or end-of-line comment: a       */
    /* member's key, key gap, colon, colon gap, value and value gap; an       */
    /* element's value and value gap; a comment item's comment.  Null gaps    */
    /* take the document's colon style, or nothing.                           */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     item : the item.                                                   */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the item is written.                                        */
    /*------------------------------------------------------------------------*/
    public void Vjw_WriteItemContent(VoorheesItem item)
    {
        if (item.Vi_iKind == VoorheesItem.VI_COMMENT)
        {   /* A comment item: its text. */
            Vjw_PutTrivia(item.Vi_sComment);
            return;
        }

        if (item.Vi_iKind == VoorheesItem.VI_MEMBER)
        {   /* A member: key and colon first. */
            if (item.Vi_sKeyRaw != null)
            {   /* The key as written. */
                Vjw_PutText(item.Vi_sKeyRaw);
            }
            else
            {   /* The key generated from its text. */
                Vjw_PutText(VoorheesJson.Vjs_QuoteString(item.Vi_sKey));
            }

            if (item.Vi_sKeyGap != null)
            {   /* The gap as written. */
                Vjw_PutTrivia(item.Vi_sKeyGap);
            }
            else
            {   /* The document's style. */
                Vjw_PutText(Vjw_doc.Vd_sKeyGap);
            }

            Vjw_PutText(":");

            if (item.Vi_sColonGap != null)
            {   /* The gap as written. */
                Vjw_PutTrivia(item.Vi_sColonGap);
            }
            else
            {   /* The document's style. */
                Vjw_PutText(Vjw_doc.Vd_sColonGap);
            }
        }

        Vjw_WriteValue(item);
        Vjw_PutTrivia(item.Vi_sValueGap);
    }

    /*------------------------------------------------------------------------*/
    /* Vjw_WriteValue:                                                        */
    /*                                                                        */
    /* Writes an item's value: a container through Vjw_WriteContainer; a      */
    /* plain value as its stored token, or generated from its text (a string  */
    /* quoted and escaped, anything else as its text).                        */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     item : the member or element.                                      */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the value is written.                                       */
    /*------------------------------------------------------------------------*/
    void Vjw_WriteValue(VoorheesItem item)
    {
        VoorheesNode node;                               // the value

        node = item.Vi_node;
        if (node.Vn_IsContainer())
        {   /* An object or array. */
            Vjw_WriteContainer(node, item);
            return;
        }

        if (node.Vn_sRaw != null)
        {   /* The token as written. */
            Vjw_PutText(node.Vn_sRaw);
        }
        else if (node.Vn_iKind == VoorheesJson.VJS_STRING)
        {   /* A string: quoted and escaped. */
            Vjw_PutText(VoorheesJson.Vjs_QuoteString(node.Vn_sText));
        }
        else
        {   /* A number, boolean or null: its text is its token. */
            Vjw_PutText(node.Vn_sText);
        }
    }
}
