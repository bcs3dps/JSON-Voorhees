/*----------------------------------------------------------------------------*/
/* VoorheesDocument.cs                                                        */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/*----------------------------------------------------------------------------*/
/* Voorhees' own model of a JSON document, and everything that changes it.    */
/* A file is read into this model on load (VoorheesJson.Vjs_Parse) and        */
/* turned back into text only on save (VoorheesJson.Vjs_Write); in between,   */
/* the editor works on these classes alone.                                   */
/*                                                                            */
/* THE MODEL IS LOSSLESS.  Every character of the file is held somewhere:     */
/* values and keys as they were written (escapes included), and all the       */
/* text between them -- whitespace, line breaks and comments -- as TRIVIA     */
/* strings on the items.  Writing an unedited document therefore gives back   */
/* the file byte for byte, by construction.  A trivia field that is null      */
/* means "generate it": the writer makes it up from the document's style      */
/* (indentation, line ending, colon spacing), which is how edited and new     */
/* items get a sensible layout while everything else stays untouched.         */
/*                                                                            */
/* Classes, each with its own name prefix:                                    */
/*     VoorheesNode     (Vn_)  : a value (string, number, boolean, null), a   */
/*                               container (object, array), or the whole      */
/*                               document, which is a container of its own.   */
/*     VoorheesItem     (Vi_)  : one entry in a container: an object member,  */
/*                               an array element, or a comment, with the     */
/*                               trivia around it.                            */
/*     VoorheesChange   (Vch_) : one recorded change: set, insert or remove   */
/*                               an item.                                     */
/*     VoorheesEditStep (Ves_) : the changes one user action made; one undo   */
/*                               step.                                        */
/*     VoorheesDocument (Vd_)  : the document: its model, its file format,    */
/*                               undo and redo, search and replace.           */
/*                                                                            */
/* Addressing.  An item is found by its POSITION PATH: the item positions     */
/* from the document down.  The document node is the empty path; [k] is the   */
/* document's k-th item (a top-level comment or the top value); [k, j] is     */
/* the j-th item inside the top value; and so on.  Comments take positions    */
/* like any other item, so a path names exactly one item even where an        */
/* object repeats a key.                                                      */
/*                                                                            */
/* Items are treated as IMMUTABLE once they are in the document: a change     */
/* replaces an item with a new one (copy-on-edit), and the change record      */
/* keeps both.  Undo applies the recorded changes backwards; nothing is       */
/* copied or compared as text.  Containers' item lists are the only thing     */
/* that changes in place, always through a recorded change.                   */
/*                                                                            */
/* Nothing here touches the user interface or the disk, so search and         */
/* Replace All can run on a worker thread behind the progress dialog.         */
/*----------------------------------------------------------------------------*/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

/*----------------------------------------------------------------------------*/
/* VoorheesNode                                                               */
/*                                                                            */
/* A value, a container, or the document.  Vn_iKind says which: the core      */
/* knows only VN_DOCUMENT, the whole file with no brackets of its own; every  */
/* other kind belongs to a format module, which numbers its kinds in a range  */
/* of its own (VoorheesJson.VJS_STRING and so on) and says what each kind     */
/* keeps in the fields below.  In general:                                    */
/*     a plain value : Vn_sText is the value as the user sees and edits it;   */
/*                     Vn_sRaw is its token exactly as written, or null when  */
/*                     generating it from Vn_sText gives exactly that.        */
/*     a container   : Vn_lstItems, its items in file order (comments         */
/*                     included); Vn_sClose; Vn_bOneLine.                     */
/* Vn_sClose is the text between the last item and the container's end (its   */
/* closing bracket, or the end of the file for the document); null means      */
/* generate it.  Vn_bOneLine records that the container had no line break     */
/* inside it in the file; it decides how generated layout looks, never how    */
/* explicit text is written.                                                  */
/*----------------------------------------------------------------------------*/
class VoorheesNode
{
    /*------------------------------------------------------------------------*/
    /* The one kind of node the core itself knows.  Format modules' kinds     */
    /* start at 10, so they never meet it.                                    */
    /*------------------------------------------------------------------------*/

    public const int VN_DOCUMENT = 1;                    // the whole file: top-level items, no brackets

    public int Vn_iKind;                                 // kind of node: VN_DOCUMENT or a format module's kind
    public string Vn_sText;                              // leaf: decoded text (see the class header); null for containers
    public string Vn_sRaw;                               // leaf: the token as written, or null to generate it from Vn_sText
    public List<VoorheesItem> Vn_lstItems;               // container: items in file order; null for leaves
    public string Vn_sClose;                             // container: text before the closing bracket (or end of file); null = generate
    public bool Vn_bOneLine;                             // container: no line break inside it in the file

    /*------------------------------------------------------------------------*/
    /* Vn_NewContainer:                                                       */
    /*                                                                        */
    /* An empty container of a kind, as the editor makes one: no items, the   */
    /* closing text left to be generated, and NOT one-line, so it writes as   */
    /* {} or [] while empty and is laid out one item per line once items      */
    /* are added.  (The reader sets the layout fields itself for containers   */
    /* that come from a file.)                                                */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iKind : VN_DOCUMENT or a format module's container kind (JSON:     */
    /*             VoorheesJson.VJS_OBJECT, VJS_ARRAY).                       */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesNode : the empty container.                                */
    /*------------------------------------------------------------------------*/
    public static VoorheesNode Vn_NewContainer(int iKind)
    {
        VoorheesNode node;                               // the new container

        node = new VoorheesNode();
        node.Vn_iKind = iKind;
        node.Vn_lstItems = new List<VoorheesItem>();
        node.Vn_sClose = null;
        node.Vn_bOneLine = false;

        /* Empty, ready for items. */
        return(node);
    }

    /*------------------------------------------------------------------------*/
    /* Vn_NewLeaf:                                                            */
    /*                                                                        */
    /* A plain value of a given kind with given text and no raw token, so     */
    /* the writer generates its token from the text.  The text is taken as    */
    /* it is: the format module that makes the value is responsible for it    */
    /* being valid for its kind (JSON: VoorheesJson.Vjs_MakeNumber for a      */
    /* number, "true"/"false" for a boolean, "null" for null).                */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iKind : the value's kind (a format module's kind id).              */
    /*     sText : the value's text.                                          */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesNode : the value.                                          */
    /*------------------------------------------------------------------------*/
    public static VoorheesNode Vn_NewLeaf(int iKind, string sText)
    {
        VoorheesNode node;                               // the new value

        /* The text exactly as given: the format module that makes the   */
        /* value supplies what its kind reads as (JSON's null passes     */
        /* "null").                                                      */
        node = new VoorheesNode();
        node.Vn_iKind = iKind;
        node.Vn_sRaw = null;
        node.Vn_sText = sText;

        /* The value, its token to be generated. */
        return(node);
    }

    /*------------------------------------------------------------------------*/
    /* Vn_IsContainer:                                                        */
    /*                                                                        */
    /* True for the nodes that hold items: the document and every format's    */
    /* containers (JSON objects and arrays, sections, registry keys).         */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true when Vn_lstItems is in use.                            */
    /*------------------------------------------------------------------------*/
    public bool Vn_IsContainer()
    {
        /* Only containers carry an item list (Vn_NewContainer gives one),   */
        /* whatever their format calls them: object, array, section, key.    */
        return(Vn_lstItems != null);
    }

    /*------------------------------------------------------------------------*/
    /* Vn_Count:                                                              */
    /*                                                                        */
    /* How many items a node holds, comments included: the number of tree     */
    /* rows below it, and the range of positions in a path.                   */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     int : items in a container; 0 for a plain value.                   */
    /*------------------------------------------------------------------------*/
    public int Vn_Count()
    {
        if (Vn_lstItems == null)
        {   /* A plain value: no items. */
            return(0);
        }

        /* Members, elements and comments. */
        return(Vn_lstItems.Count);
    }

    /*------------------------------------------------------------------------*/
    /* Vn_ElementIndex:                                                       */
    /*                                                                        */
    /* The array index of the item at a position: how many value items come   */
    /* before it.  Comments take positions in the item list but not indexes   */
    /* in the array, so "[3]" in the tree is the fourth VALUE, whatever       */
    /* comments sit between.                                                  */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iPos : the item's position.                                        */
    /*                                                                        */
    /* Returns:                                                               */
    /*     int : the number of value items before iPos.                       */
    /*------------------------------------------------------------------------*/
    public int Vn_ElementIndex(int iPos)
    {
        int iIndex;                                      // value items counted so far
        int i;

        iIndex = 0;
        for (i = 0; i < iPos && i < Vn_lstItems.Count; i++)
        {
            if (Vn_lstItems[i].Vi_IsValue())
            {   /* A value: it takes an index; a comment does not. */
                iIndex++;
            }
        }

        /* The index the item at iPos has. */
        return(iIndex);
    }

    /*------------------------------------------------------------------------*/
    /* Vn_LastValuePos:                                                       */
    /*                                                                        */
    /* The position of the last value item in a container: the one item that  */
    /* is written WITHOUT a comma after it.                                   */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     int : the position, or -1 when the container holds no values.      */
    /*------------------------------------------------------------------------*/
    public int Vn_LastValuePos()
    {
        int i;

        for (i = Vn_lstItems.Count - 1; i >= 0; i--)
        {
            if (Vn_lstItems[i].Vi_IsValue())
            {   /* The last value, searching from the end. */
                return(i);
            }
        }

        /* Only comments, or nothing at all. */
        return(-1);
    }

    /*-------------------------------------------------------------------------*/
    /* Vn_KeyCount:                                                            */
    /*                                                                         */
    /* How many members of this container have a given key, compared the way   */
    /* its format compares keys: 0 if none, 1 for a unique key, more for a     */
    /* repeated one.                                                           */
    /*                                                                         */
    /* Arguments:                                                              */
    /*     sKey     : the decoded key.                                         */
    /*     comparer : how keys compare here (VoorheesDocument.Vd_KeyComparer). */
    /*                                                                         */
    /* Returns:                                                                */
    /*     int : the number of members with that key; 0 for a plain value or   */
    /*           a container that holds no members (a JSON array).             */
    /*-------------------------------------------------------------------------*/
    public int Vn_KeyCount(string sKey, StringComparer comparer)
    {
        int iCount;                                      // members with sKey so far
        int i;

        iCount = 0;
        if (Vn_lstItems != null)
        {   /* A container: count the members with this key (only containers that hold members have any). */
            for (i = 0; i < Vn_lstItems.Count; i++)
            {
                if (Vn_lstItems[i].Vi_iKind == VoorheesItem.VI_MEMBER
                    && comparer.Equals(Vn_lstItems[i].Vi_sKey, sKey))
                {   /* A member with the same key. */
                    iCount++;
                }
            }
        }

        /* How many members carry the key. */
        return(iCount);
    }

    /*-------------------------------------------------------------------------*/
    /* Vn_KeyCounts:                                                           */
    /*                                                                         */
    /* The number of members with each key in this container, all at once,     */
    /* for labelling every member without a scan per member.  The dictionary   */
    /* compares keys with the comparer given, so looking up any spelling the   */
    /* format treats as the same key finds its count.                          */
    /*                                                                         */
    /* Arguments:                                                              */
    /*     comparer : how keys compare here (VoorheesDocument.Vd_KeyComparer). */
    /*                                                                         */
    /* Returns:                                                                */
    /*     Dictionary<string, int> : key to number of members; empty for a     */
    /*                               plain value or a container that holds     */
    /*                               no members (a JSON array).                */
    /*-------------------------------------------------------------------------*/
    public Dictionary<string, int> Vn_KeyCounts(StringComparer comparer)
    {
        Dictionary<string, int> dictCounts;              // key to members with it
        VoorheesItem item;                               // the item being tallied
        int iCount;                                      // count so far for one key
        int i;

        dictCounts = new Dictionary<string, int>(comparer);
        if (Vn_lstItems != null)
        {   /* A container: tally every member's key (only containers that hold members have any). */
            for (i = 0; i < Vn_lstItems.Count; i++)
            {
                item = Vn_lstItems[i];
                if (item.Vi_iKind != VoorheesItem.VI_MEMBER)
                {   /* A comment: no key. */
                    continue;
                }

                if (dictCounts.TryGetValue(item.Vi_sKey, out iCount))
                {   /* Seen before: another copy. */
                    dictCounts[item.Vi_sKey] = iCount + 1;
                }
                else
                {   /* First copy. */
                    dictCounts[item.Vi_sKey] = 1;
                }
            }
        }

        /* Every key with its count. */
        return(dictCounts);
    }

    /*------------------------------------------------------------------------*/
    /* Vn_CountEntries:                                                       */
    /*                                                                        */
    /* How many entries this node is in the tree, counting itself and every   */
    /* item below it once (comments included): the number of steps a walk     */
    /* over everything takes, for searching and for progress estimates.       */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     long : 1 for a plain value; more for a non-empty container.        */
    /*------------------------------------------------------------------------*/
    public long Vn_CountEntries()
    {
        long lCount;                                     // entries counted so far
        int i;

        /* This node, then everything inside it. */
        lCount = 1;
        if (Vn_lstItems != null)
        {   /* A container: a comment is one entry, a value its own count. */
            for (i = 0; i < Vn_lstItems.Count; i++)
            {
                if (Vn_lstItems[i].Vi_IsValue())
                {   /* A value: itself and everything in it. */
                    lCount += Vn_lstItems[i].Vi_node.Vn_CountEntries();
                }
                else
                {   /* A comment: one entry. */
                    lCount++;
                }
            }
        }

        /* This node and all below it. */
        return(lCount);
    }

    /*------------------------------------------------------------------------*/
    /* Vn_ContainsComments:                                                   */
    /*                                                                        */
    /* Whether this node or anything inside it carries a comment of any       */
    /* kind: a comment item, a comment at the end of a line or after an       */
    /* opening bracket, or one in an odd place.  A document that does is not  */
    /* standard JSON (it is "JSONC"), which the editor tells the user.        */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true at the first comment found.                            */
    /*------------------------------------------------------------------------*/
    public bool Vn_ContainsComments()
    {
        VoorheesItem item;                               // the item being checked
        int i;

        if (Vn_lstItems == null)
        {   /* A plain value cannot hold a comment. */
            return(false);
        }

        for (i = 0; i < Vn_lstItems.Count; i++)
        {
            item = Vn_lstItems[i];
            if (item.Vi_HasAnyComment())
            {   /* This item carries one. */
                return(true);
            }

            if (item.Vi_IsValue() && item.Vi_node.Vn_ContainsComments())
            {   /* Something inside its value does. */
                return(true);
            }
        }

        /* None anywhere in this node. */
        return(false);
    }

    /*------------------------------------------------------------------------*/
    /* Vn_Clone:                                                              */
    /*                                                                        */
    /* A deep copy: the node and everything inside it, sharing nothing        */
    /* mutable with the original, so changing one never shows in the other    */
    /* (Copy and Paste rely on this).  Strings are shared: they never         */
    /* change.  Layout is copied as it is; the format clears it when the copy */
    /* is to be laid out afresh (VoorheesFormat.Vf_ResetLayout).              */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesNode : the copy.                                           */
    /*------------------------------------------------------------------------*/
    public VoorheesNode Vn_Clone()
    {
        VoorheesNode copy;                               // the copy being built
        int i;

        copy = new VoorheesNode();
        copy.Vn_iKind = Vn_iKind;
        copy.Vn_sText = Vn_sText;
        copy.Vn_sRaw = Vn_sRaw;
        copy.Vn_sClose = Vn_sClose;
        copy.Vn_bOneLine = Vn_bOneLine;

        if (Vn_lstItems != null)
        {   /* A container: a copy of every item, each with its own copy of its value. */
            copy.Vn_lstItems = new List<VoorheesItem>(Vn_lstItems.Count);
            for (i = 0; i < Vn_lstItems.Count; i++)
            {
                copy.Vn_lstItems.Add(Vn_lstItems[i].Vi_CloneDeep());
            }
        }

        /* An independent copy. */
        return(copy);
    }

    /*------------------------------------------------------------------------*/
    /* Vn_SameAs:                                                             */
    /*                                                                        */
    /* Whether putting this value in place of another would change the        */
    /* document's DATA: plain values are the same when their kind and text    */
    /* agree; a container only when it is the very same object.               */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     other : the value to compare with.                                 */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true when nothing would change.                             */
    /*------------------------------------------------------------------------*/
    public bool Vn_SameAs(VoorheesNode other)
    {
        if (ReferenceEquals(this, other))
        {   /* The same object: nothing changes. */
            return(true);
        }

        if (other == null || Vn_IsContainer() || other.Vn_IsContainer())
        {   /* Nothing to compare with, or two different containers. */
            return(false);
        }

        /* Two plain values: the same when kind and text agree. */
        return(Vn_iKind == other.Vn_iKind && Vn_sText == other.Vn_sText);
    }

    /*------------------------------------------------------------------------*/
    /* Vn_DisplayValue:                                                       */
    /*                                                                        */
    /* A plain value as text, for labels, the Value box and searching: the    */
    /* string itself, the number exactly as written, "true", "false" or       */
    /* "null".                                                                */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the text; "" for a container, which has no single value.  */
    /*------------------------------------------------------------------------*/
    public string Vn_DisplayValue()
    {
        if (Vn_IsContainer())
        {   /* Containers are shown through their items. */
            return("");
        }

        /* The leaf's own text. */
        return(Vn_sText);
    }

}

/*----------------------------------------------------------------------------*/
/* VoorheesItem                                                               */
/*                                                                            */
/* One entry in a container, with the text around it.  A container is         */
/* written as its opening bracket, then for each item:                        */
/*                                                                            */
/*     Vi_sLead  content  [","]  Vi_sEolGap Vi_sEolComment                    */
/*                                                                            */
/* then the container's Vn_sClose and its closing bracket.  The comma is not  */
/* stored: it is written after every value item except the container's last   */
/* one (comments never take commas), which is what keeps commas right when    */
/* items are inserted and removed.  Content by kind:                          */
/*                                                                            */
/*     VI_MEMBER  : key  Vi_sKeyGap  ":"  Vi_sColonGap  value  Vi_sValueGap   */
/*     VI_ELEMENT : value  Vi_sValueGap                                       */
/*     VI_COMMENT : Vi_sComment                                               */
/*                                                                            */
/* where a container value is written as its opening bracket, then            */
/* Vi_sOpenGap Vi_sOpenComment (a comment on the same line as the bracket),   */
/* then its items as above.                                                   */
/*                                                                            */
/* Field meanings:                                                            */
/*     Vi_sLead        : whitespace and line breaks from the end of the       */
/*                       previous separator (or the opening bracket) to the   */
/*                       start of this item.  Never holds a comment: a        */
/*                       comment on a line of its own is an item of its own.  */
/*     Vi_sKey         : the decoded key (members only).                      */
/*     Vi_sKeyRaw      : the key token as written, or null to generate it     */
/*                       from Vi_sKey (no escapes were used).                 */
/*     Vi_sKeyGap      : text between the key and the colon.                  */
/*     Vi_sColonGap    : text between the colon and the value.                */
/*     Vi_node         : the value (members and elements).                    */
/*     Vi_sValueGap    : text between the value and its comma (or, for the    */
/*                       last value, before its end-of-line comment).         */
/*     Vi_sEolGap      : whitespace before the end-of-line comment.           */
/*     Vi_sEolComment  : the comment(s) on the same line after the value      */
/*                       and its comma, with their // or /- -/ markers; null  */
/*                       for none.                                            */
/*     Vi_sOpenGap     : whitespace between a container value's opening       */
/*                       bracket and Vi_sOpenComment.                         */
/*     Vi_sOpenComment : comment(s) on the same line as a container value's   */
/*                       opening bracket; null for none.                      */
/*     Vi_sComment     : a comment item's text, markers included; it may      */
/*                       hold several comments that start on one line.        */
/*     Vi_bOddComments : the item has comments only the Raw box can edit:     */
/*                       one inside a gap, several where one is expected, or  */
/*                       a line comment on the side of a container that is    */
/*                       not its line (see Vi_LineComment).  The format       */
/*                       works it out: its reader as it reads, and            */
/*                       Vd_Finalize for every item an action puts in.        */
/*                                                                            */
/* Every text field is null to mean "generate it from the document's style"   */
/* (comment fields: null means no comment).                                   */
/*                                                                            */
/* IMMUTABLE once in a document: changes are made by building a new item      */
/* with the Vi_With... functions and replacing the old one through a          */
/* recorded change.  A new item made from an old one shares its value node    */
/* unless the value itself is replaced.                                       */
/*----------------------------------------------------------------------------*/
class VoorheesItem
{
    /*------------------------------------------------------------------------*/
    /* Kinds of item.                                                         */
    /*------------------------------------------------------------------------*/

    public const int VI_MEMBER = 0;                      // object member: key and value
    public const int VI_ELEMENT = 1;                     // array element, or the document's top value
    public const int VI_COMMENT = 2;                     // a comment on a line of its own

    public int Vi_iKind;                                 // VI_ kind of item
    public string Vi_sLead;                              // whitespace before the item; null = generate
    public string Vi_sKey;                               // member: decoded key
    public string Vi_sKeyRaw;                            // member: key token as written; null = generate from Vi_sKey
    public string Vi_sKeyGap;                            // member: text between key and colon; null = style
    public string Vi_sColonGap;                          // member: text between colon and value; null = style
    public VoorheesNode Vi_node;                         // member/element: the value
    public string Vi_sValueGap;                          // member/element: text after the value, before the comma; null = none
    public string Vi_sEolGap;                            // whitespace before the end-of-line comment; null = generate
    public string Vi_sEolComment;                        // end-of-line comment(s), markers included; null = none
    public string Vi_sOpenGap;                           // container value: whitespace after the opening bracket; null = generate
    public string Vi_sOpenComment;                       // container value: comment(s) after the opening bracket; null = none
    public string Vi_sComment;                           // comment item: the comment text, markers included
    public bool Vi_bOddComments;                         // comments here that only the Raw box can edit

    /*------------------------------------------------------------------------*/
    /* Vi_NewMember:                                                          */
    /*                                                                        */
    /* A new object member with every piece of layout to be generated.        */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sKey : the decoded key.                                            */
    /*     node : the value.                                                  */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesItem : the member.                                         */
    /*------------------------------------------------------------------------*/
    public static VoorheesItem Vi_NewMember(string sKey, VoorheesNode node)
    {
        VoorheesItem item;                               // the new member

        item = new VoorheesItem();
        item.Vi_iKind = VI_MEMBER;
        item.Vi_sKey = sKey;
        item.Vi_node = node;

        /* All text fields null: laid out by the writer. */
        return(item);
    }

    /*------------------------------------------------------------------------*/
    /* Vi_NewElement:                                                         */
    /*                                                                        */
    /* A new array element (or top value) with its layout to be generated.    */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     node : the value.                                                  */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesItem : the element.                                        */
    /*------------------------------------------------------------------------*/
    public static VoorheesItem Vi_NewElement(VoorheesNode node)
    {
        VoorheesItem item;                               // the new element

        item = new VoorheesItem();
        item.Vi_iKind = VI_ELEMENT;
        item.Vi_node = node;

        /* All text fields null: laid out by the writer. */
        return(item);
    }

    /*------------------------------------------------------------------------*/
    /* Vi_NewComment:                                                         */
    /*                                                                        */
    /* A new comment item, its lead to be generated.                          */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sRaw : the comment with its markers (the format builds one from    */
    /*            plain text).  Its odd-comment flag is set when it goes into */
    /*            a document (Vd_Finalize).                                   */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesItem : the comment item.                                   */
    /*------------------------------------------------------------------------*/
    public static VoorheesItem Vi_NewComment(string sRaw)
    {
        VoorheesItem item;                               // the new comment item

        item = new VoorheesItem();
        item.Vi_iKind = VI_COMMENT;
        item.Vi_sComment = sRaw;

        /* On a line of its own, wherever it is put. */
        return(item);
    }

    /*------------------------------------------------------------------------*/
    /* Vi_IsValue:                                                            */
    /*                                                                        */
    /* True for members and elements, the items that hold a value; false for  */
    /* comments.                                                              */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true when Vi_node is in use.                                */
    /*------------------------------------------------------------------------*/
    public bool Vi_IsValue()
    {
        /* Everything but a comment holds a value. */
        return(Vi_iKind != VI_COMMENT);
    }

    /*------------------------------------------------------------------------*/
    /* Vi_LineCommentIsOpen:                                                  */
    /*                                                                        */
    /* Which comment is "the comment on this item's line", shown after its    */
    /* label and edited in the Comment box.  For a container written over     */
    /* several lines, the item's line is the line of its opening bracket, so  */
    /* it is the comment after the bracket (Vi_sOpenComment).  For anything   */
    /* else -- a plain value, or a container on one line -- it is the         */
    /* comment at the end of the line, after the comma (Vi_sEolComment).      */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true when the line comment is Vi_sOpenComment.              */
    /*------------------------------------------------------------------------*/
    public bool Vi_LineCommentIsOpen()
    {
        /* Only a multi-line container's line is its opening line. */
        return(Vi_IsValue() && Vi_node.Vn_IsContainer() && !Vi_node.Vn_bOneLine);
    }

    /*------------------------------------------------------------------------*/
    /* Vi_LineComment:                                                        */
    /*                                                                        */
    /* The comment on this item's line (see Vi_LineCommentIsOpen), raw.       */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the comment with its markers, or null for none.           */
    /*------------------------------------------------------------------------*/
    public string Vi_LineComment()
    {
        if (Vi_LineCommentIsOpen())
        {   /* A multi-line container: the comment after its opening bracket. */
            return(Vi_sOpenComment);
        }

        /* Anything else: the comment at the end of its line. */
        return(Vi_sEolComment);
    }

    /*------------------------------------------------------------------------*/
    /* Vi_HasAnyComment:                                                      */
    /*                                                                        */
    /* Whether this item itself (not its value's items) carries a comment of  */
    /* any kind.                                                              */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true for a comment item, an end-of-line or open comment, or */
    /*            a comment in a gap.                                         */
    /*------------------------------------------------------------------------*/
    public bool Vi_HasAnyComment()
    {
        /* Any of the places a comment can be. */
        return(Vi_iKind == VI_COMMENT || Vi_sEolComment != null
            || Vi_sOpenComment != null || Vi_bOddComments);
    }

    /*------------------------------------------------------------------------*/
    /* Vi_Copy:                                                               */
    /*                                                                        */
    /* A shallow copy: every field the same, the value node SHARED.  The      */
    /* starting point of every Vi_With... function.                           */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesItem : the copy.                                           */
    /*------------------------------------------------------------------------*/
    public VoorheesItem Vi_Copy()
    {
        VoorheesItem copy;                               // the copy being built

        copy = new VoorheesItem();
        copy.Vi_iKind = Vi_iKind;
        copy.Vi_sLead = Vi_sLead;
        copy.Vi_sKey = Vi_sKey;
        copy.Vi_sKeyRaw = Vi_sKeyRaw;
        copy.Vi_sKeyGap = Vi_sKeyGap;
        copy.Vi_sColonGap = Vi_sColonGap;
        copy.Vi_node = Vi_node;
        copy.Vi_sValueGap = Vi_sValueGap;
        copy.Vi_sEolGap = Vi_sEolGap;
        copy.Vi_sEolComment = Vi_sEolComment;
        copy.Vi_sOpenGap = Vi_sOpenGap;
        copy.Vi_sOpenComment = Vi_sOpenComment;
        copy.Vi_sComment = Vi_sComment;
        copy.Vi_bOddComments = Vi_bOddComments;

        /* Same fields, same value object. */
        return(copy);
    }

    /*------------------------------------------------------------------------*/
    /* Vi_CloneDeep:                                                          */
    /*                                                                        */
    /* A copy with its own deep copy of the value, for Copy and Paste.        */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesItem : the copy, sharing nothing mutable with this item.   */
    /*------------------------------------------------------------------------*/
    public VoorheesItem Vi_CloneDeep()
    {
        VoorheesItem copy;                               // the copy being built

        copy = Vi_Copy();
        if (Vi_node != null)
        {   /* A value: copy it too. */
            copy.Vi_node = Vi_node.Vn_Clone();
        }

        /* Independent of this item. */
        return(copy);
    }

    /*------------------------------------------------------------------------*/
    /* Vi_WithKey:                                                            */
    /*                                                                        */
    /* This member with a different key, written fresh (no raw token).  The   */
    /* value node is shared.                                                  */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sKey : the new decoded key.                                        */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesItem : the new item.                                       */
    /*------------------------------------------------------------------------*/
    public VoorheesItem Vi_WithKey(string sKey)
    {
        VoorheesItem copy;                               // the renamed member

        copy = Vi_Copy();
        copy.Vi_sKey = sKey;
        copy.Vi_sKeyRaw = null;

        /* Everything else as it was. */
        return(copy);
    }

    /*------------------------------------------------------------------------*/
    /* Vi_WithNode:                                                           */
    /*                                                                        */
    /* This member or element with a different value.  The item's own text    */
    /* is kept.  When the value stops being a container, any comment after    */
    /* its opening bracket has nowhere to go, so it moves to the end of the   */
    /* line rather than being lost (unless one is already there, in which     */
    /* case the two are joined).                                              */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     node : the new value.                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesItem : the new item.                                       */
    /*------------------------------------------------------------------------*/
    public VoorheesItem Vi_WithNode(VoorheesNode node)
    {
        VoorheesItem copy;                               // the item with its new value

        copy = Vi_Copy();
        copy.Vi_node = node;

        if (copy.Vi_sOpenComment != null && !node.Vn_IsContainer())
        {   /* No opening bracket any more: keep the comment at the end of the line. */
            if (copy.Vi_sEolComment == null)
            {   /* No end-of-line comment yet: it becomes that. */
                copy.Vi_sEolComment = copy.Vi_sOpenComment;
            }
            else
            {   /* Already one: put both there. */
                copy.Vi_sEolComment = copy.Vi_sOpenComment + " "
                    + copy.Vi_sEolComment;
            }
            copy.Vi_sOpenGap = null;
            copy.Vi_sOpenComment = null;
        }

        /* The new value in the old item's place and layout (its odd-      */
        /* comment flag is worked out again as it goes in: Vd_Finalize).   */
        return(copy);
    }

    /*------------------------------------------------------------------------*/
    /* Vi_WithLineComment:                                                    */
    /*                                                                        */
    /* This item with a different comment on its line (see                    */
    /* Vi_LineCommentIsOpen for which side that is).  Removing the comment    */
    /* removes its gap too; adding one where there was none generates the     */
    /* gap.                                                                   */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sRaw : the new comment with its markers, or null for none.         */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesItem : the new item.                                       */
    /*------------------------------------------------------------------------*/
    public VoorheesItem Vi_WithLineComment(string sRaw)
    {
        VoorheesItem copy;                               // the item with its new comment

        copy = Vi_Copy();
        if (Vi_LineCommentIsOpen())
        {   /* A multi-line container: the comment after its opening bracket. */
            if (sRaw == null || copy.Vi_sOpenComment == null)
            {   /* Removed, or new: no gap to keep. */
                copy.Vi_sOpenGap = null;
            }
            copy.Vi_sOpenComment = sRaw;
        }
        else
        {   /* Anything else: the comment at the end of the line. */
            if (sRaw == null || copy.Vi_sEolComment == null)
            {   /* Removed, or new: no gap to keep. */
                copy.Vi_sEolGap = null;
            }
            copy.Vi_sEolComment = sRaw;
        }

        /* Everything else as it was (the odd-comment flag is worked out   */
        /* again as it goes in: Vd_Finalize).                              */
        return(copy);
    }

    /*------------------------------------------------------------------------*/
    /* Vi_WithComment:                                                        */
    /*                                                                        */
    /* This comment item with different text.                                 */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sRaw : the new comment with its markers.                           */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesItem : the new item, in the same place and layout.         */
    /*------------------------------------------------------------------------*/
    public VoorheesItem Vi_WithComment(string sRaw)
    {
        VoorheesItem copy;                               // the item with its new text

        copy = Vi_Copy();
        copy.Vi_sComment = sRaw;

        /* Same lead, new text (the odd-comment flag is worked out again as   */
        /* it goes in: Vd_Finalize).                                          */
        return(copy);
    }

    /*------------------------------------------------------------------------*/
    /* Vi_WithLead:                                                           */
    /*                                                                        */
    /* This item with a different lead: used when an item takes over the      */
    /* layout of the item it replaces (a Raw edit keeps the old lead) or of a */
    /* neighbour.                                                             */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sLead : the new lead, or null to generate one.                     */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesItem : the new item.                                       */
    /*------------------------------------------------------------------------*/
    public VoorheesItem Vi_WithLead(string sLead)
    {
        VoorheesItem copy;                               // the item with its new lead

        copy = Vi_Copy();
        copy.Vi_sLead = sLead;

        /* Everything else as it was. */
        return(copy);
    }
}

/*----------------------------------------------------------------------------*/
/* VoorheesChange                                                             */
/*                                                                            */
/* One change, recorded with everything needed to make it again (redo) or     */
/* take it back (undo).  Kinds, by the container at Vch_lstPath and the       */
/* position Vch_iPos in it:                                                   */
/*     VCH_SETITEM : the item at the position goes from Vch_itemOld to        */
/*                   Vch_itemNew.  Covers every edit of an existing item:     */
/*                   rename, value or type change, comment edits, Raw edits,  */
/*                   and replacing the top value.                             */
/*     VCH_INSERT  : Vch_itemNew is put in at the position.                   */
/*     VCH_REMOVE  : Vch_itemOld is taken out from the position.              */
/* Undoing a change does its opposite: SETITEM back, REMOVE what was          */
/* inserted, INSERT what was removed.                                         */
/*----------------------------------------------------------------------------*/
class VoorheesChange
{
    /*------------------------------------------------------------------------*/
    /* Kinds of change.                                                       */
    /*------------------------------------------------------------------------*/

    public const int VCH_SETITEM = 0;                    // replace the item at a position
    public const int VCH_INSERT = 1;                     // add an item at a position
    public const int VCH_REMOVE = 2;                     // take out the item at a position

    public int Vch_iKind;                                // VCH_ kind
    public List<int> Vch_lstPath;                        // position path of the container changed (empty = the document)
    public int Vch_iPos;                                 // item position in that container
    public VoorheesItem Vch_itemOld;                     // item before (SETITEM) or the item removed (REMOVE)
    public VoorheesItem Vch_itemNew;                     // item after (SETITEM) or the item inserted (INSERT)

    /*------------------------------------------------------------------------*/
    /* Vch_Create:                                                            */
    /*                                                                        */
    /* Builds a change record; the three Vch_New... functions are the ones    */
    /* callers use.  The path is copied, so the caller may go on to change    */
    /* its own list.                                                          */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iKind   : VCH_ kind.                                               */
    /*     lstPath : container position path.                                 */
    /*     iPos    : item position in the container.                          */
    /*     itemOld : item before / item removed, or null.                     */
    /*     itemNew : item after / item inserted, or null.                     */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesChange : the record.                                       */
    /*------------------------------------------------------------------------*/
    static VoorheesChange Vch_Create(int iKind, List<int> lstPath, int iPos,
        VoorheesItem itemOld, VoorheesItem itemNew)
    {
        VoorheesChange ch;                               // the record being built

        ch = new VoorheesChange();
        ch.Vch_iKind = iKind;
        ch.Vch_lstPath = new List<int>(lstPath);
        ch.Vch_iPos = iPos;
        ch.Vch_itemOld = itemOld;
        ch.Vch_itemNew = itemNew;

        /* Ready to apply and keep. */
        return(ch);
    }

    /*------------------------------------------------------------------------*/
    /* Vch_NewSetItem:                                                        */
    /*                                                                        */
    /* A change replacing one item with another.                              */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     lstPath : container position path.                                 */
    /*     iPos    : the item's position.                                     */
    /*     itemOld : the item there now.                                      */
    /*     itemNew : the item to put there.                                   */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesChange : the record.                                       */
    /*------------------------------------------------------------------------*/
    public static VoorheesChange Vch_NewSetItem(List<int> lstPath, int iPos,
        VoorheesItem itemOld, VoorheesItem itemNew)
    {
        /* Both items kept, for undo and redo. */
        return(Vch_Create(VCH_SETITEM, lstPath, iPos, itemOld, itemNew));
    }

    /*------------------------------------------------------------------------*/
    /* Vch_NewInsert:                                                         */
    /*                                                                        */
    /* A change adding an item.                                               */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     lstPath : container position path.                                 */
    /*     iPos    : where it goes, 0 to the container's count (the end).     */
    /*     itemNew : the item.                                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesChange : the record.                                       */
    /*------------------------------------------------------------------------*/
    public static VoorheesChange Vch_NewInsert(List<int> lstPath, int iPos,
        VoorheesItem itemNew)
    {
        /* Undone by removing it again. */
        return(Vch_Create(VCH_INSERT, lstPath, iPos, null, itemNew));
    }

    /*------------------------------------------------------------------------*/
    /* Vch_NewRemove:                                                         */
    /*                                                                        */
    /* A change taking an item out.                                           */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     lstPath : container position path.                                 */
    /*     iPos    : the item's position.                                     */
    /*     itemOld : the item there now.                                      */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesChange : the record.                                       */
    /*------------------------------------------------------------------------*/
    public static VoorheesChange Vch_NewRemove(List<int> lstPath, int iPos,
        VoorheesItem itemOld)
    {
        /* Undone by putting it back where it was. */
        return(Vch_Create(VCH_REMOVE, lstPath, iPos, itemOld, null));
    }

    /*------------------------------------------------------------------------*/
    /* Vch_TargetPath:                                                        */
    /*                                                                        */
    /* The position path of the item this change is about.  This is where     */
    /* the editor puts the selection after making or undoing it (the tree     */
    /* falls back to the nearest ancestor when it no longer exists).          */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     List<int> : a new list holding the path.                           */
    /*------------------------------------------------------------------------*/
    public List<int> Vch_TargetPath()
    {
        List<int> lstTarget;                             // the path being built

        lstTarget = new List<int>(Vch_lstPath);
        lstTarget.Add(Vch_iPos);

        /* The container's path plus the position. */
        return(lstTarget);
    }
}

/*----------------------------------------------------------------------------*/
/* VoorheesEditStep                                                           */
/*                                                                            */
/* One undo step: every change a single user action made, in the order they   */
/* were made (Replace All can make thousands).  Undo reverses them last       */
/* first.                                                                     */
/*----------------------------------------------------------------------------*/
class VoorheesEditStep
{
    public List<VoorheesChange> Ves_lstChanges;          // changes in the order made
    public List<int> Ves_lstFocus;                       // what to select after doing (or redoing) the step

    /*------------------------------------------------------------------------*/
    /* Ves_Create:                                                            */
    /*                                                                        */
    /* An empty step, ready for changes.                                      */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     lstFocus : position path to select once the step is done or        */
    /*                redone; null to select the first change's target.       */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesEditStep : the step.                                       */
    /*------------------------------------------------------------------------*/
    public static VoorheesEditStep Ves_Create(List<int> lstFocus)
    {
        VoorheesEditStep step;                           // the step being built

        step = new VoorheesEditStep();
        step.Ves_lstChanges = new List<VoorheesChange>();
        if (lstFocus != null)
        {   /* Keep a copy of the selection to come back to. */
            step.Ves_lstFocus = new List<int>(lstFocus);
        }

        /* Empty, ready to fill. */
        return(step);
    }

    /*------------------------------------------------------------------------*/
    /* Ves_FocusAfterDo:                                                      */
    /*                                                                        */
    /* What to select once the step has been done or redone: the focus given  */
    /* when it was made, else its first change's target.                      */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     List<int> : a position path (a new list).                          */
    /*------------------------------------------------------------------------*/
    public List<int> Ves_FocusAfterDo()
    {
        if (Ves_lstFocus != null)
        {   /* Chosen when the step was made. */
            return(new List<int>(Ves_lstFocus));
        }

        if (Ves_lstChanges.Count > 0)
        {   /* The first thing it changed. */
            return(Ves_lstChanges[0].Vch_TargetPath());
        }

        /* An empty step: the document. */
        return(new List<int>());
    }

    /*------------------------------------------------------------------------*/
    /* Ves_FocusAfterUndo:                                                    */
    /*                                                                        */
    /* What to select once the step has been undone: its first change's       */
    /* target, which after undoing is the item as it was.                     */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     List<int> : a position path (a new list).                          */
    /*------------------------------------------------------------------------*/
    public List<int> Ves_FocusAfterUndo()
    {
        if (Ves_lstChanges.Count > 0)
        {   /* The first thing it had changed. */
            return(Ves_lstChanges[0].Vch_TargetPath());
        }

        /* An empty step: the document. */
        return(new List<int>());
    }
}

/*----------------------------------------------------------------------------*/
/* VoorheesDocument                                                           */
/*                                                                            */
/* The document being edited and how its file is written.  The model is       */
/* Vd_root, a VN_DOCUMENT node.  The file format -- encoding and byte order   */
/* mark -- is kept so a save writes the same bytes; the STYLE -- line         */
/* ending, indentation unit, colon spacing -- is used only for text the       */
/* writer generates (explicit text keeps its own).                            */
/*                                                                            */
/* Every change goes through Vd_Apply, inside a step started with             */
/* Vd_BeginStep and finished with Vd_EndStep, which is what gives undo and    */
/* redo.  The tree view is told about each change separately by the editor;   */
/* this class never touches the user interface.                               */
/*----------------------------------------------------------------------------*/
class VoorheesDocument
{
    /*------------------------------------------------------------------------*/
    /* Style used when a document has none of its own (new documents, and     */
    /* files that give nothing to go by).                                     */
    /*------------------------------------------------------------------------*/

    public const string VD_DEFAULTINDENT = "  ";         // one level of indentation: two spaces
    public const string VD_DEFAULTNEWLINE = "\r\n";      // Windows line ending
    public const string VD_DEFAULTKEYGAP = "";           // nothing between a key and its colon
    public const string VD_DEFAULTCOLONGAP = " ";        // one space between the colon and the value

    public int Vd_iFormat;                               // the file's format: a VoorheesFormat.VF_ id
    public VoorheesNode Vd_root;                         // the document (VN_DOCUMENT node)
    public string Vd_sPath;                              // its file, or null when never saved
    public Encoding Vd_encoding;                         // file encoding (without BOM behaviour)
    public bool Vd_bBom;                                 // write a byte order mark first
    public string Vd_sNewline;                           // style: "\r\n" or "\n"
    public string Vd_sIndentUnit;                        // style: one level of indentation
    public string Vd_sKeyGap;                            // style: text between a key and its colon
    public string Vd_sColonGap;                          // style: text between a colon and its value
    public string Vd_sDelimiter;                         // style, line formats: text between a key and its value, delimiter included ("=", " = ", ": ")
    public string Vd_sCommentPrefix;                     // style, line formats: the marker new comments start with (";", "#")
    public bool Vd_bModified;                            // differs from the file on disk
    Stack<VoorheesEditStep> Vd_stkUndo;                  // steps that can be undone, newest on top
    Stack<VoorheesEditStep> Vd_stkRedo;                  // steps undone and available to redo

    /*------------------------------------------------------------------------*/
    /* Vd_Create:                                                             */
    /*                                                                        */
    /* A document around a model.  Null format or style details take the      */
    /* defaults: UTF-8 without a BOM, CRLF, two-space indentation, "key":     */
    /* value spacing; the delimiter and comment marker of the line formats    */
    /* take their format's defaults (Vf_ApplyDefaultStyle), which the         */
    /* format's reader then sets from the file.                               */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iFormat     : its format, a VoorheesFormat.VF_ id.                 */
    /*     root        : the VN_DOCUMENT node.                                */
    /*     sPath       : its file, or null.                                   */
    /*     encoding    : the file's encoding, or null.                        */
    /*     bBom        : the file started with a byte order mark.             */
    /*     sNewline    : the file's line ending, or null.                     */
    /*     sIndentUnit : one level of its indentation, or null.               */
    /*     sKeyGap     : its usual text between key and colon, or null.       */
    /*     sColonGap   : its usual text between colon and value, or null.     */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesDocument : unmodified, with no undo history.               */
    /*------------------------------------------------------------------------*/
    public static VoorheesDocument Vd_Create(int iFormat, VoorheesNode root,
        string sPath, Encoding encoding, bool bBom, string sNewline,
        string sIndentUnit, string sKeyGap, string sColonGap)
    {
        VoorheesDocument doc;                            // the document being built

        doc = new VoorheesDocument();
        doc.Vd_iFormat = iFormat;
        doc.Vd_root = root;
        doc.Vd_sPath = sPath;
        doc.Vd_bBom = bBom;
        doc.Vd_stkUndo = new Stack<VoorheesEditStep>();
        doc.Vd_stkRedo = new Stack<VoorheesEditStep>();
        doc.Vd_bModified = false;

        if (encoding != null)
        {   /* Loaded file: keep its encoding. */
            doc.Vd_encoding = encoding;
        }
        else
        {   /* New document: UTF-8. */
            doc.Vd_encoding = new UTF8Encoding(false);
        }

        if (sNewline != null)
        {   /* The file's line ending. */
            doc.Vd_sNewline = sNewline;
        }
        else
        {   /* None known: Windows line endings. */
            doc.Vd_sNewline = VD_DEFAULTNEWLINE;
        }

        if (sIndentUnit != null)
        {   /* The file's indentation. */
            doc.Vd_sIndentUnit = sIndentUnit;
        }
        else
        {   /* None known: the default. */
            doc.Vd_sIndentUnit = VD_DEFAULTINDENT;
        }

        if (sKeyGap != null && sColonGap != null)
        {   /* The file's colon spacing. */
            doc.Vd_sKeyGap = sKeyGap;
            doc.Vd_sColonGap = sColonGap;
        }
        else
        {   /* None known: "key": value. */
            doc.Vd_sKeyGap = VD_DEFAULTKEYGAP;
            doc.Vd_sColonGap = VD_DEFAULTCOLONGAP;
        }

        /* The format's defaults for the style it alone uses (the line    */
        /* formats' delimiter and comment marker); its reader overrides   */
        /* them with what it detects in the file.                         */
        VoorheesFormat.Vf_ApplyDefaultStyle(doc);

        /* Ready to edit. */
        return(doc);
    }

    /*------------------------------------------------------------------------*/
    /* Vd_CreateNew:                                                          */
    /*                                                                        */
    /* A new, never-saved document of a format, as that format makes one      */
    /* (VoorheesFormat.Vf_NewDocument; JSON: an empty object) in its default  */
    /* style: File > New.                                                     */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iFormat : the VoorheesFormat.VF_ format.                           */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesDocument : the new document.                               */
    /*------------------------------------------------------------------------*/
    public static VoorheesDocument Vd_CreateNew(int iFormat)
    {
        /* The format builds it. */
        return(VoorheesFormat.Vf_NewDocument(iFormat));
    }

    /*------------------------------------------------------------------------*/
    /* Vd_NodeAt:                                                             */
    /*                                                                        */
    /* The node at a position path: the document node for the empty path,     */
    /* else the value of the item the path names.                             */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     lstPath : positions from the document down.                        */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesNode : the node, or null when the path leads nowhere or    */
    /*                    names a comment (which has no node).                */
    /*------------------------------------------------------------------------*/
    public VoorheesNode Vd_NodeAt(List<int> lstPath)
    {
        VoorheesNode node;                               // node reached so far
        VoorheesItem item;                               // item at the current step
        int i;

        node = Vd_root;
        for (i = 0; i < lstPath.Count; i++)
        {
            if (node == null || lstPath[i] < 0 || lstPath[i] >= node.Vn_Count())
            {   /* No item at this step. */
                return(null);
            }

            item = node.Vn_lstItems[lstPath[i]];
            if (!item.Vi_IsValue())
            {   /* A comment has no node. */
                return(null);
            }
            node = item.Vi_node;
        }

        /* The node at the end of the path. */
        return(node);
    }

    /*------------------------------------------------------------------------*/
    /* Vd_ItemAt:                                                             */
    /*                                                                        */
    /* The item a position path names.                                        */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     lstPath : positions from the document down (not empty).            */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesItem : the item, or null for the empty path or a path that */
    /*                    leads nowhere.                                      */
    /*------------------------------------------------------------------------*/
    public VoorheesItem Vd_ItemAt(List<int> lstPath)
    {
        VoorheesNode container;                          // the item's container
        int iPos;                                        // its position there

        if (lstPath.Count == 0)
        {   /* The document itself is not an item. */
            return(null);
        }

        container = Vd_NodeAt(lstPath.GetRange(0, lstPath.Count - 1));
        iPos = lstPath[lstPath.Count - 1];
        if (container == null || !container.Vn_IsContainer() || iPos < 0
            || iPos >= container.Vn_Count())
        {   /* No container there, or no item at that position. */
            return(null);
        }

        /* The item. */
        return(container.Vn_lstItems[iPos]);
    }

    /*------------------------------------------------------------------------*/
    /* Vd_InitialPath:                                                        */
    /*                                                                        */
    /* The entry to select when the document is first shown, and whether to   */
    /* open it (VoorheesFormat.Vf_InitialPath: for JSON the top value,        */
    /* opened one level).                                                     */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     bExpand : set to true when the entry is to be opened.              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     List<int> : the entry's path (a new list), or null when there is   */
    /*                 nothing to select (an empty document).                 */
    /*------------------------------------------------------------------------*/
    public List<int> Vd_InitialPath(out bool bExpand)
    {
        /* The format decides. */
        return(VoorheesFormat.Vf_InitialPath(this, out bExpand));
    }

    /*------------------------------------------------------------------------*/
    /* Vd_KeyComparer:                                                        */
    /*                                                                        */
    /* How the keys of a container's members compare in this document's       */
    /* format (VoorheesFormat.Vf_KeyComparer): for duplicate marks, the       */
    /* duplicate refusals of rename, insert and paste, and free-key choice.   */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     container : the container.                                         */
    /*                                                                        */
    /* Returns:                                                               */
    /*     StringComparer : the comparison.                                   */
    /*------------------------------------------------------------------------*/
    public StringComparer Vd_KeyComparer(VoorheesNode container)
    {
        /* The format decides, per container. */
        return(VoorheesFormat.Vf_KeyComparer(Vd_iFormat, container));
    }

    /*------------------------------------------------------------------------*/
    /* Vd_KeyCounts:                                                          */
    /*                                                                        */
    /* The number of members with each key in a container, keys compared the  */
    /* way its format compares them (Vn_KeyCounts with Vd_KeyComparer): what  */
    /* the tree labels repeated keys from.                                    */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     container : the container.                                         */
    /*                                                                        */
    /* Returns:                                                               */
    /*     Dictionary<string, int> : key to number of members (it compares    */
    /*                               keys with the same comparer).            */
    /*------------------------------------------------------------------------*/
    public Dictionary<string, int> Vd_KeyCounts(VoorheesNode container)
    {
        /* One pass over the container. */
        return(container.Vn_KeyCounts(Vd_KeyComparer(container)));
    }

    /*------------------------------------------------------------------------*/
    /* Vd_ExtraCopies:                                                        */
    /*                                                                        */
    /* Counts, through the whole document, the members that are a second or   */
    /* later copy of a key in the same container (keys compared by the        */
    /* format: Vd_KeyComparer): the members a reader that keeps one copy per  */
    /* key would lose.                                                        */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     int : number of extra copies; 0 means every key is unique.         */
    /*------------------------------------------------------------------------*/
    public int Vd_ExtraCopies()
    {
        /* From the document node down. */
        return(Vd_ExtraCopiesIn(Vd_root));
    }

    /*------------------------------------------------------------------------*/
    /* Vd_ExtraCopiesIn:                                                      */
    /*                                                                        */
    /* Vd_ExtraCopies for one node and everything inside it: in a container,  */
    /* every member past the first with its key is extra; then the values     */
    /* inside are counted the same way.                                       */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     node : the node.                                                   */
    /*                                                                        */
    /* Returns:                                                               */
    /*     int : extra copies in node and below; 0 for a plain value.         */
    /*------------------------------------------------------------------------*/
    int Vd_ExtraCopiesIn(VoorheesNode node)
    {
        Dictionary<string, int> dictCounts;              // this container's keys and their counts
        int iCount;                                      // extra copies found so far
        int iMembers;                                    // members in this container
        int i;

        if (!node.Vn_IsContainer())
        {   /* A plain value holds no members. */
            return(0);
        }

        /* Members past the first copy of each key (comments not counted). */
        dictCounts = Vd_KeyCounts(node);
        iMembers = 0;
        for (i = 0; i < node.Vn_lstItems.Count; i++)
        {
            if (node.Vn_lstItems[i].Vi_iKind == VoorheesItem.VI_MEMBER)
            {   /* A member. */
                iMembers++;
            }
        }
        iCount = iMembers - dictCounts.Count;

        /* Add whatever the values inside hold. */
        for (i = 0; i < node.Vn_lstItems.Count; i++)
        {
            if (node.Vn_lstItems[i].Vi_IsValue())
            {   /* A value: count inside it. */
                iCount += Vd_ExtraCopiesIn(node.Vn_lstItems[i].Vi_node);
            }
        }

        /* Total for this node and everything inside it. */
        return(iCount);
    }

    /*------------------------------------------------------------------------*/
    /* Vd_HasComments:                                                        */
    /*                                                                        */
    /* Whether the document holds a comment anywhere, which makes it JSONC    */
    /* rather than standard JSON.                                             */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true when it does.                                          */
    /*------------------------------------------------------------------------*/
    public bool Vd_HasComments()
    {
        /* A walk of the whole document, stopping at the first comment. */
        return(Vd_root.Vn_ContainsComments());
    }

    /*------------------------------------------------------------------------*/
    /* Vd_CanUndo:                                                            */
    /*                                                                        */
    /* Whether there is a step to undo.                                       */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true when Vd_Undo would do something.                       */
    /*------------------------------------------------------------------------*/
    public bool Vd_CanUndo()
    {
        /* Anything on the undo stack. */
        return(Vd_stkUndo.Count > 0);
    }

    /*------------------------------------------------------------------------*/
    /* Vd_CanRedo:                                                            */
    /*                                                                        */
    /* Whether there is an undone step to redo.                               */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true when Vd_Redo would do something.                       */
    /*------------------------------------------------------------------------*/
    public bool Vd_CanRedo()
    {
        /* Anything on the redo stack. */
        return(Vd_stkRedo.Count > 0);
    }

    /*------------------------------------------------------------------------*/
    /* Vd_BeginStep:                                                          */
    /*                                                                        */
    /* Starts an undo step for one user action.  The changes it makes are     */
    /* applied through Vd_Apply, then the step is finished with Vd_EndStep.   */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     lstFocus : what to select after redoing it; null for its first     */
    /*                change's target.                                        */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesEditStep : the empty step.                                 */
    /*------------------------------------------------------------------------*/
    public VoorheesEditStep Vd_BeginStep(List<int> lstFocus)
    {
        /* Nothing recorded yet. */
        return(VoorheesEditStep.Ves_Create(lstFocus));
    }

    /*------------------------------------------------------------------------*/
    /* Vd_Apply:                                                              */
    /*                                                                        */
    /* Makes one change to the document and records it in a step.  The        */
    /* change must be valid against the document as it is now (the callers    */
    /* check keys and positions before building it).  The item the change     */
    /* puts in, if any, is finalised by the format first (Vd_Finalize), so    */
    /* every item that enters the document through an action is complete; the */
    /* recorded change keeps that finalised item, so redo puts back exactly   */
    /* the same one.                                                          */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     step : the step being built.                                       */
    /*     ch   : the change (not yet applied).                               */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the document is changed and the change recorded.            */
    /*------------------------------------------------------------------------*/
    public void Vd_Apply(VoorheesEditStep step, VoorheesChange ch)
    {
        if (ch.Vch_itemNew != null)
        {   /* A set or an insert: the format's last word on the new item. */
            ch.Vch_itemNew = Vd_Finalize(Vd_NodeAt(ch.Vch_lstPath),
                ch.Vch_itemNew);
        }

        Vd_ApplyChange(ch, false);
        step.Ves_lstChanges.Add(ch);
    }

    /*------------------------------------------------------------------------*/
    /* Vd_Finalize:                                                           */
    /*                                                                        */
    /* Hands an item that is about to go into the document to its format      */
    /* (VoorheesFormat.Vf_FinalizeItem), which works out what depends on the  */
    /* format -- JSON recomputes the odd-comment flag -- and may normalise    */
    /* format details.  The item must not be in a document yet: the format    */
    /* may change it in place.                                                */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     container : the container it is going into.                        */
    /*     item      : the item.                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesItem : the item to put in (normally the same object).      */
    /*------------------------------------------------------------------------*/
    VoorheesItem Vd_Finalize(VoorheesNode container, VoorheesItem item)
    {
        /* The format decides. */
        return(VoorheesFormat.Vf_FinalizeItem(this, container, item));
    }

    /*------------------------------------------------------------------------*/
    /* Vd_EndStep:                                                            */
    /*                                                                        */
    /* Finishes a step.  A step that changed anything becomes the newest      */
    /* undo step, ends the redo history (a new change makes the undone ones   */
    /* meaningless) and marks the document modified.  An empty step (an edit  */
    /* that changed nothing) leaves everything as it was.                     */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     step : the step built with Vd_Apply.                               */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true when the step changed the document.                    */
    /*------------------------------------------------------------------------*/
    public bool Vd_EndStep(VoorheesEditStep step)
    {
        if (step.Ves_lstChanges.Count == 0)
        {   /* Nothing changed: no undo step, still as saved (or not). */
            return(false);
        }

        Vd_stkUndo.Push(step);
        Vd_stkRedo.Clear();
        Vd_bModified = true;

        /* A real change. */
        return(true);
    }

    /*------------------------------------------------------------------------*/
    /* Vd_AbandonStep:                                                        */
    /*                                                                        */
    /* Takes back a step that is being built, instead of finishing it: its    */
    /* changes are reversed, last first, and nothing is recorded.  For an     */
    /* action made of several parts (the edit panel's Save: value, key,       */
    /* comment) whose later part is refused, so the user's Save is all or     */
    /* nothing.                                                               */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     step : the unfinished step.                                        */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the document is as it was before the step began, and the    */
    /*            step is emptied.                                            */
    /*------------------------------------------------------------------------*/
    public void Vd_AbandonStep(VoorheesEditStep step)
    {
        int i;

        for (i = step.Ves_lstChanges.Count - 1; i >= 0; i--)
        {
            Vd_ApplyChange(step.Ves_lstChanges[i], true);
        }
        step.Ves_lstChanges.Clear();
    }

    /*------------------------------------------------------------------------*/
    /* Vd_Undo:                                                               */
    /*                                                                        */
    /* Takes back the newest step: its changes are reversed, last first, and  */
    /* the step moves to the redo stack.  Undo history starts at the last     */
    /* load or save, so when none is left the document matches its file       */
    /* again and is no longer modified.                                       */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesEditStep : the step undone, for the tree to follow         */
    /*                        (reversed, last change first); null when there  */
    /*                        was nothing to undo.                            */
    /*------------------------------------------------------------------------*/
    public VoorheesEditStep Vd_Undo()
    {
        VoorheesEditStep step;                           // the step being undone
        int i;

        if (Vd_stkUndo.Count == 0)
        {   /* Nothing to undo. */
            return(null);
        }

        step = Vd_stkUndo.Pop();
        for (i = step.Ves_lstChanges.Count - 1; i >= 0; i--)
        {
            Vd_ApplyChange(step.Ves_lstChanges[i], true);
        }

        Vd_stkRedo.Push(step);
        Vd_bModified = (Vd_stkUndo.Count > 0);

        /* What was undone. */
        return(step);
    }

    /*------------------------------------------------------------------------*/
    /* Vd_Redo:                                                               */
    /*                                                                        */
    /* Makes the most recently undone step again, first change first, and     */
    /* puts it back on the undo stack.                                        */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesEditStep : the step redone, for the tree to follow; null   */
    /*                        when there was nothing to redo.                 */
    /*------------------------------------------------------------------------*/
    public VoorheesEditStep Vd_Redo()
    {
        VoorheesEditStep step;                           // the step being redone
        int i;

        if (Vd_stkRedo.Count == 0)
        {   /* Nothing to redo. */
            return(null);
        }

        step = Vd_stkRedo.Pop();
        for (i = 0; i < step.Ves_lstChanges.Count; i++)
        {
            Vd_ApplyChange(step.Ves_lstChanges[i], false);
        }

        Vd_stkUndo.Push(step);
        Vd_bModified = true;

        /* What was redone. */
        return(step);
    }

    /*------------------------------------------------------------------------*/
    /* Vd_MarkSaved:                                                          */
    /*                                                                        */
    /* The document has been written to its file: it is no longer modified,   */
    /* and the undo and redo history end here (undo lasts until the next      */
    /* save).                                                                 */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sPath : the file it was written to, which it now belongs to.       */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the path, modified flag and history are updated.            */
    /*------------------------------------------------------------------------*/
    public void Vd_MarkSaved(string sPath)
    {
        Vd_sPath = sPath;
        Vd_bModified = false;
        Vd_stkUndo.Clear();
        Vd_stkRedo.Clear();
    }

    /*------------------------------------------------------------------------*/
    /* Vd_ApplyChange:                                                        */
    /*                                                                        */
    /* Makes one recorded change, forwards or backwards, on the document      */
    /* alone.                                                                 */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     ch       : the change.                                             */
    /*     bReverse : true to undo it, false to make it.                      */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the container's item list is changed.                       */
    /*------------------------------------------------------------------------*/
    void Vd_ApplyChange(VoorheesChange ch, bool bReverse)
    {
        List<VoorheesItem> lstItems;                     // the container's items

        lstItems = Vd_NodeAt(ch.Vch_lstPath).Vn_lstItems;

        switch (ch.Vch_iKind)
        {
            case VoorheesChange.VCH_SETITEM:
                /* One item replaced, by position. */
                if (bReverse)
                {   /* Undo: the old item back. */
                    lstItems[ch.Vch_iPos] = ch.Vch_itemOld;
                }
                else
                {   /* Do: the new item. */
                    lstItems[ch.Vch_iPos] = ch.Vch_itemNew;
                }
                break;

            case VoorheesChange.VCH_INSERT:
                /* An item added; undone, it is taken out again. */
                if (bReverse)
                {   /* Undo: remove what was inserted. */
                    lstItems.RemoveAt(ch.Vch_iPos);
                }
                else
                {   /* Do: insert it. */
                    lstItems.Insert(ch.Vch_iPos, ch.Vch_itemNew);
                }
                break;

            case VoorheesChange.VCH_REMOVE:
                /* An item taken out; undone, it goes back where it was. */
                if (bReverse)
                {   /* Undo: put it back. */
                    lstItems.Insert(ch.Vch_iPos, ch.Vch_itemOld);
                }
                else
                {   /* Do: remove it. */
                    lstItems.RemoveAt(ch.Vch_iPos);
                }
                break;
        }
    }

    /*------------------------------------------------------------------------*/
    /* EDITOR ACTIONS.  Each user action that changes the document, built     */
    /* from changes here rather than in the form, so the editor and the test  */
    /* program run the same code.  Each records its changes in the step it is */
    /* given (Vd_BeginStep / Vd_EndStep stay with the caller, so one step can */
    /* hold several actions) and returns null on success or the reason it was */
    /* refused, for the user; a refused action changes nothing.  An action    */
    /* that would change nothing records nothing, so it makes no undo step.   */
    /*------------------------------------------------------------------------*/

    /*------------------------------------------------------------------------*/
    /* Vd_SplitPath:                                                          */
    /*                                                                        */
    /* An item's container and position, from its path, with the checks       */
    /* every action needs.                                                    */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     lstPath   : the item's position path.                              */
    /*     lstParent : set to the container's path.                           */
    /*     container : set to the container.                                  */
    /*     iPos      : set to the item's position.                            */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesItem : the item, or null when the path names none.         */
    /*------------------------------------------------------------------------*/
    VoorheesItem Vd_SplitPath(List<int> lstPath, out List<int> lstParent,
        out VoorheesNode container, out int iPos)
    {
        lstParent = null;
        container = null;
        iPos = -1;
        if (lstPath == null || lstPath.Count == 0)
        {   /* The document itself is not an item. */
            return(null);
        }

        lstParent = lstPath.GetRange(0, lstPath.Count - 1);
        container = Vd_NodeAt(lstParent);
        iPos = lstPath[lstPath.Count - 1];
        if (container == null || !container.Vn_IsContainer() || iPos < 0
            || iPos >= container.Vn_Count())
        {   /* No item there. */
            return(null);
        }

        /* The item. */
        return(container.Vn_lstItems[iPos]);
    }

    /*------------------------------------------------------------------------*/
    /* Vd_SetItem:                                                            */
    /*                                                                        */
    /* Records a SETITEM change if the new item differs from the old one.     */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     step      : the step.                                              */
    /*     lstParent : the container's path.                                  */
    /*     iPos      : the item's position.                                   */
    /*     itemOld   : the item there now.                                    */
    /*     itemNew   : its replacement (may be the same object).              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the change is applied and recorded, or nothing happens.     */
    /*------------------------------------------------------------------------*/
    void Vd_SetItem(VoorheesEditStep step, List<int> lstParent, int iPos,
        VoorheesItem itemOld, VoorheesItem itemNew)
    {
        if (!ReferenceEquals(itemOld, itemNew))
        {   /* A real change. */
            Vd_Apply(step, VoorheesChange.Vch_NewSetItem(lstParent, iPos,
                itemOld, itemNew));
        }
    }

    /*------------------------------------------------------------------------*/
    /* Vd_ActSetValue:                                                        */
    /*                                                                        */
    /* Gives a member or element (or the top value) a new value, keeping its  */
    /* key and layout: the edit panel's Save, in-place editing, a type        */
    /* change.  A plain value equal to the old one changes nothing; a value   */
    /* the format does not allow at that place is refused (Vf_CheckItem).     */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     step    : the step.                                                */
    /*     lstPath : the item's position path.                                */
    /*     node    : the new value.                                           */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : null, or why it was refused.                              */
    /*------------------------------------------------------------------------*/
    public string Vd_ActSetValue(VoorheesEditStep step, List<int> lstPath,
        VoorheesNode node)
    {
        VoorheesItem item;                               // the item
        VoorheesItem newItem;                            // the item with its new value
        List<int> lstParent;                             // its container's path
        VoorheesNode container;                          // its container
        string sProblem;                                 // why the format refuses it
        int iPos;                                        // its position

        item = Vd_SplitPath(lstPath, out lstParent, out container, out iPos);
        if (item == null || !item.Vi_IsValue())
        {   /* Nothing there, or a comment, which has no value. */
            return("That entry has no value to set.");
        }

        if (node.Vn_SameAs(item.Vi_node))
        {   /* The same value: nothing to do. */
            return(null);
        }

        newItem = item.Vi_WithNode(node);
        sProblem = VoorheesFormat.Vf_CheckItem(this, container, newItem);
        if (sProblem != null)
        {   /* The format does not allow that value there. */
            return(sProblem);
        }

        Vd_SetItem(step, lstParent, iPos, item, newItem);

        /* Done. */
        return(null);
    }

    /*------------------------------------------------------------------------*/
    /* Vd_ActRename:                                                          */
    /*                                                                        */
    /* Gives an object member a new key: Rename Key and the edit panel's Key  */
    /* box.  Refused for an empty key or one already used in the object       */
    /* (Vd_RenameProblem).                                                    */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     step    : the step.                                                */
    /*     lstPath : the member's position path.                              */
    /*     sKey    : the new key.                                             */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : null, or why it was refused.                              */
    /*------------------------------------------------------------------------*/
    public string Vd_ActRename(VoorheesEditStep step, List<int> lstPath,
        string sKey)
    {
        VoorheesItem item;                               // the member
        List<int> lstParent;                             // its container's path
        VoorheesNode container;                          // its object
        int iPos;                                        // its position
        string sProblem;                                 // why the key is refused

        item = Vd_SplitPath(lstPath, out lstParent, out container, out iPos);
        sProblem = Vd_RenameProblem(container, iPos, sKey);
        if (sProblem != null)
        {   /* Not a member, or an unusable key. */
            return(sProblem);
        }

        if (sKey == item.Vi_sKey)
        {   /* Unchanged. */
            return(null);
        }

        Vd_SetItem(step, lstParent, iPos, item, item.Vi_WithKey(sKey));

        /* Done. */
        return(null);
    }

    /*------------------------------------------------------------------------*/
    /* Vd_ActSetLineComment:                                                  */
    /*                                                                        */
    /* Sets, changes or removes the comment on an item's line (the Comment    */
    /* box; see VoorheesItem.Vi_LineCommentIsOpen for which side that is),    */
    /* where the format allows one (Vf_HasLineComment).  Unchanged words keep */
    /* the comment exactly as written; changed words keep its style           */
    /* (Vf_RemakeComment); a new comment is in the format's syntax            */
    /* (Vf_MakeComment: for JSON "// words" unless the line goes on after it, */
    /* when it is a block comment).  Empty words remove it.                   */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     step    : the step.                                                */
    /*     lstPath : the item's position path.                                */
    /*     sText   : the comment's words; "" or null to remove it.            */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : null, or why it was refused.                              */
    /*------------------------------------------------------------------------*/
    public string Vd_ActSetLineComment(VoorheesEditStep step,
        List<int> lstPath, string sText)
    {
        VoorheesItem item;                               // the item
        List<int> lstParent;                             // its container's path
        VoorheesNode container;                          // its container
        int iPos;                                        // its position
        string sOld;                                     // its line comment now
        string sNew;                                     // the new comment
        string sError;                                   // why the words cannot be a comment

        item = Vd_SplitPath(lstPath, out lstParent, out container, out iPos);
        if (item == null
            || !VoorheesFormat.Vf_HasLineComment(this, container, item))
        {   /* Nothing there, or an entry with no line comment (a comment entry is edited through its text). */
            return("That entry has no line comment.");
        }

        sOld = item.Vi_LineComment();
        if (string.IsNullOrEmpty(sText))
        {   /* Remove it (if there is one). */
            if (sOld != null)
            {   /* There is: take it away. */
                Vd_SetItem(step, lstParent, iPos, item,
                    item.Vi_WithLineComment(null));
            }
            return(null);
        }

        if (sOld != null)
        {   /* Change it, in its own style. */
            if (!VoorheesFormat.Vf_IsSingleComment(Vd_iFormat, sOld))
            {   /* Several comments: only Raw can edit them. */
                return("This line holds several comments; edit them in the "
                    + "Raw box.");
            }
            sNew = VoorheesFormat.Vf_RemakeComment(this, container, iPos, sOld,
                sText, true, out sError);
        }
        else
        {   /* A new comment, in the syntax that fits its place. */
            sNew = VoorheesFormat.Vf_MakeComment(this, container, iPos, sText,
                true, out sError);
        }

        if (sNew == null)
        {   /* The words cannot be a comment. */
            return(sError);
        }

        if (sNew != sOld)
        {   /* A real change. */
            Vd_SetItem(step, lstParent, iPos, item,
                item.Vi_WithLineComment(sNew));
        }

        /* Done. */
        return(null);
    }

    /*------------------------------------------------------------------------*/
    /* Vd_ActSetCommentText:                                                  */
    /*                                                                        */
    /* Changes a comment entry's words (the Value box for a comment).         */
    /* Unchanged words keep it exactly as written; changed words keep its     */
    /* style (Vf_RemakeComment: for JSON a line comment that gains a line     */
    /* break, or that something would follow on its line, becomes a block     */
    /* one).                                                                  */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     step    : the step.                                                */
    /*     lstPath : the comment entry's position path.                       */
    /*     sText   : its new words.                                           */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : null, or why it was refused.                              */
    /*------------------------------------------------------------------------*/
    public string Vd_ActSetCommentText(VoorheesEditStep step,
        List<int> lstPath, string sText)
    {
        VoorheesItem item;                               // the comment entry
        List<int> lstParent;                             // its container's path
        VoorheesNode container;                          // its container
        int iPos;                                        // its position
        string sNew;                                     // the new comment
        string sError;                                   // why the words cannot be a comment

        item = Vd_SplitPath(lstPath, out lstParent, out container, out iPos);
        if (item == null || item.Vi_iKind != VoorheesItem.VI_COMMENT)
        {   /* Not a comment entry. */
            return("That entry is not a comment.");
        }

        if (!VoorheesFormat.Vf_IsSingleComment(Vd_iFormat, item.Vi_sComment))
        {   /* Several comments: only Raw can edit them. */
            return("This entry holds several comments; edit them in the Raw "
                + "box.");
        }

        sNew = VoorheesFormat.Vf_RemakeComment(this, container, iPos,
            item.Vi_sComment, sText, false, out sError);
        if (sNew == null)
        {   /* The words cannot be a comment. */
            return(sError);
        }

        if (sNew != item.Vi_sComment)
        {   /* A real change. */
            Vd_SetItem(step, lstParent, iPos, item, item.Vi_WithComment(sNew));
        }

        /* Done. */
        return(null);
    }

    /*------------------------------------------------------------------------*/
    /* Vd_ActInsert:                                                          */
    /*                                                                        */
    /* Puts an item into a container at a position.  The format first makes   */
    /* it fit there, or refuses it (Vf_FitItem: for JSON a member put into an */
    /* array becomes an element, an element put into an object becomes a      */
    /* member with a free key, and the document node takes comments only).    */
    /* A member's key must be valid for the format (Vf_KeyProblem) and not    */
    /* already used in the container (Voorhees never creates a duplicate), so */
    /* callers pick one with Vd_UnusedKey.                                    */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     step          : the step.                                          */
    /*     lstContainer  : the container's position path.                     */
    /*     iPos          : where the item goes; -1 for the end (as the format */
    /*                     sees it, Vf_EndPosition: above a Klipper-family    */
    /*                     auto-saved block, which nothing may follow).       */
    /*     item          : the item (not in any document; not changed).       */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : null, or why it was refused.                              */
    /*------------------------------------------------------------------------*/
    public string Vd_ActInsert(VoorheesEditStep step, List<int> lstContainer,
        int iPos, VoorheesItem item)
    {
        VoorheesNode container;                          // where it goes
        VoorheesItem toInsert;                           // the item as inserted
        string sKey;                                     // a member's key ("" for none)
        string sError;                                   // why it was refused
        string sBeyond;                                  // why nothing goes past the end position
        int iEnd;                                        // the last position allowed (Vf_EndPosition)

        container = Vd_NodeAt(lstContainer);
        if (container == null || !container.Vn_IsContainer())
        {   /* Not a container. */
            return("Entries can only be added to an object or an array.");
        }

        /* The last position the format allows (the end, except above a   */
        /* Klipper-family auto-saved block, which stays last).            */
        iEnd = VoorheesFormat.Vf_EndPosition(this, container, out sBeyond);
        if (iPos < 0 || iPos > container.Vn_Count())
        {   /* At the end, as the format sees it. */
            iPos = iEnd;
        }
        else if (iPos > iEnd)
        {   /* Past something that must stay last. */
            return(sBeyond);
        }

        /* Made to fit the container, or refused. */
        toInsert = VoorheesFormat.Vf_FitItem(this, container, item, out sError);
        if (toInsert == null)
        {   /* It cannot go there. */
            return(sError);
        }

        if (toInsert.Vi_iKind == VoorheesItem.VI_MEMBER)
        {   /* A member: its key must be usable here. */
            sKey = toInsert.Vi_sKey;
            if (sKey == null)
            {   /* No key at all: as an empty one. */
                sKey = "";
            }

            sError = VoorheesFormat.Vf_KeyProblem(this, container, toInsert,
                sKey);
            if (sError != null)
            {   /* Not a valid key for the format (JSON: empty). */
                return(sError);
            }

            if (container.Vn_KeyCount(sKey, Vd_KeyComparer(container)) > 0)
            {   /* Would create a duplicate key: said in the format's words. */
                return(VoorheesFormat.Vf_DuplicateMessage(this, container,
                    toInsert, sKey));
            }
        }

        Vd_Apply(step, VoorheesChange.Vch_NewInsert(lstContainer, iPos,
            toInsert));

        /* Done. */
        return(null);
    }

    /*------------------------------------------------------------------------*/
    /* Vd_ActAddChild:                                                        */
    /*                                                                        */
    /* Add Child: a new, empty value of a kind (or a comment entry) at the    */
    /* end of a container.  The format builds the item (Vf_NewChild: for JSON */
    /* in an object a member keyed "newKey", "newKey1" ... whichever is free, */
    /* in an array an element).  A comment entry holds the words given, in    */
    /* the format's syntax (Vf_MakeComment; for JSON a // comment unless the  */
    /* line goes on after it).                                                */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     step         : the step.                                           */
    /*     lstContainer : the container's position path.                      */
    /*     iKind        : a value kind of the document's format, or -1 for a  */
    /*                    comment entry.                                      */
    /*     sCommentText : the comment's words (comment entries only).         */
    /*     lstNewPath   : set to the new item's path.                         */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : null, or why it was refused.                              */
    /*------------------------------------------------------------------------*/
    public string Vd_ActAddChild(VoorheesEditStep step, List<int> lstContainer,
        int iKind, string sCommentText, out List<int> lstNewPath)
    {
        VoorheesNode container;                          // where it goes
        VoorheesItem item;                               // the new item
        string sRaw;                                     // a new comment's text
        string sError;                                   // why it was refused
        string sBeyond;                                  // why nothing goes past the end (unused here)
        int iPos;                                        // where it goes

        lstNewPath = null;
        container = Vd_NodeAt(lstContainer);
        if (container == null || !container.Vn_IsContainer())
        {   /* Not a container. */
            return("Entries can only be added to an object or an array.");
        }

        /* The end, as the format sees it (above a Klipper-family auto-saved block). */
        iPos = VoorheesFormat.Vf_EndPosition(this, container, out sBeyond);

        if (iKind < 0)
        {   /* A comment entry. */
            sRaw = VoorheesFormat.Vf_MakeComment(this, container, iPos,
                sCommentText, false, out sError);
            if (sRaw == null)
            {   /* The words cannot be a comment. */
                return(sError);
            }
            item = VoorheesItem.Vi_NewComment(sRaw);
        }
        else
        {   /* A value: the format builds the entry for this container. */
            item = VoorheesFormat.Vf_NewChild(this, container, iKind);
        }

        sError = Vd_ActInsert(step, lstContainer, iPos, item);
        if (sError == null)
        {   /* Inserted: report where. */
            lstNewPath = new List<int>(lstContainer);
            lstNewPath.Add(iPos);
        }

        /* Done, or why not. */
        return(sError);
    }

    /*------------------------------------------------------------------------*/
    /* Vd_ActPaste:                                                           */
    /*                                                                        */
    /* Paste: a copy of a copied item at the end of a container, laid out     */
    /* afresh in this document's style (Vf_ResetLayout keeps its keys, values */
    /* and comments) and made to fit the container (Vf_FitItem: for JSON an   */
    /* element pasted into an object takes a free "newKey" variant, a member  */
    /* pasted into an array loses its key).  A member keeps its key if that   */
    /* is free, else takes the next free variant of it.                       */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     step         : the step.                                           */
    /*     lstContainer : the container's position path.                      */
    /*     copied       : the copied item (any document, or none; not         */
    /*                    changed).                                           */
    /*     iSourceFormat : the VF_ format it was copied from; another format  */
    /*                    than this document's is converted (Vf_ImportItem:   */
    /*                    single values and comments only).                   */
    /*     lstNewPath   : set to the new item's path.                         */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : null, or why it was refused.                              */
    /*------------------------------------------------------------------------*/
    public string Vd_ActPaste(VoorheesEditStep step, List<int> lstContainer,
        VoorheesItem copied, int iSourceFormat, out List<int> lstNewPath)
    {
        VoorheesNode container;                          // where it goes
        VoorheesItem item;                               // the pasted copy
        string sKey;                                     // a free key for a member
        string sError;                                   // why it was refused
        string sBeyond;                                  // why nothing goes past the end (unused here)
        int iPos;                                        // where it goes

        lstNewPath = null;
        container = Vd_NodeAt(lstContainer);
        if (container == null || !container.Vn_IsContainer())
        {   /* Not a container. */
            return("Paste goes into an object or an array.");
        }

        /* The end, as the format sees it (above a Klipper-family auto-saved block). */
        iPos = VoorheesFormat.Vf_EndPosition(this, container, out sBeyond);

        /* Copied from another format: converted first (single values and   */
        /* comments only), or refused.                                      */
        item = VoorheesFormat.Vf_ImportItem(this, iSourceFormat, container,
            copied, out sError);
        if (item == null)
        {   /* It cannot become an item of this format. */
            return(sError);
        }

        /* A private copy, laid out afresh by the format (its odd-comment   */
        /* flag is worked out as it goes in: Vd_Finalize).                  */
        item = item.Vi_CloneDeep();
        VoorheesFormat.Vf_ResetLayout(this, item);

        /* Made to fit the container, or refused. */
        item = VoorheesFormat.Vf_FitItem(this, container, item, out sError);
        if (item == null)
        {   /* It cannot go there. */
            return(sError);
        }

        if (item.Vi_iKind == VoorheesItem.VI_MEMBER)
        {   /* A member: its own key if free, else a variant of it. */
            sKey = Vd_UnusedKey(container, item.Vi_sKey);
            if (sKey != item.Vi_sKey)
            {   /* Its key is taken: it gets the variant. */
                item = item.Vi_WithKey(sKey);
            }
        }

        sError = Vd_ActInsert(step, lstContainer, iPos, item);
        if (sError == null)
        {   /* Inserted: report where. */
            lstNewPath = new List<int>(lstContainer);
            lstNewPath.Add(iPos);
        }

        /* Done, or why not. */
        return(sError);
    }

    /*------------------------------------------------------------------------*/
    /* Vd_ActRemove:                                                          */
    /*                                                                        */
    /* Delete: takes an item out, unless its format says it may not go        */
    /* (Vf_RemoveProblem: for JSON the top value, the one value a document    */
    /* holds; a top-level comment can go).                                    */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     step    : the step.                                                */
    /*     lstPath : the item's position path.                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : null, or why it was refused.                              */
    /*------------------------------------------------------------------------*/
    public string Vd_ActRemove(VoorheesEditStep step, List<int> lstPath)
    {
        VoorheesItem item;                               // the item
        List<int> lstParent;                             // its container's path
        VoorheesNode container;                          // its container
        string sProblem;                                 // why the format keeps it
        string sNewLead;                                 // the next item's new lead, if the format wants one
        int iPos;                                        // its position

        item = Vd_SplitPath(lstPath, out lstParent, out container, out iPos);
        if (item == null)
        {   /* Nothing there. */
            return("That entry no longer exists.");
        }

        sProblem = VoorheesFormat.Vf_RemoveProblem(this, container, item);
        if (sProblem != null)
        {   /* The format keeps it (JSON: the top value). */
            return(sProblem);
        }

        /* The format may need the item after it to start differently once   */
        /* it moves up (a line format's first line takes no line break).     */
        sNewLead = VoorheesFormat.Vf_LeadAfterRemove(this, container, iPos);

        Vd_Apply(step, VoorheesChange.Vch_NewRemove(lstParent, iPos, item));

        if (sNewLead != null)
        {   /* The next item, now at iPos, with its new lead: part of the same step. */
            Vd_SetItem(step, lstParent, iPos, container.Vn_lstItems[iPos],
                container.Vn_lstItems[iPos].Vi_WithLead(sNewLead));
        }

        /* Done. */
        return(null);
    }

    /*------------------------------------------------------------------------*/
    /* Vd_ActRaw:                                                             */
    /*                                                                        */
    /* A Raw edit: the item replaced by the Raw box's text, parsed strictly   */
    /* and kept exactly as typed (VoorheesJson.Vjs_ApplyRawEdit).  A member's */
    /* new key must not duplicate another member's.  Text identical to the    */
    /* item's current rendering changes nothing.                              */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     step    : the step.                                                */
    /*     lstPath : the item's position path.                                */
    /*     sText   : the Raw box's text.                                      */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : null, or why it was refused (with line and column within  */
    /*              the box).                                                 */
    /*------------------------------------------------------------------------*/
    public string Vd_ActRaw(VoorheesEditStep step, List<int> lstPath,
        string sText)
    {
        VoorheesItem item;                               // the item
        VoorheesItem newItem;                            // what the text describes
        List<int> lstParent;                             // its container's path
        VoorheesNode container;                          // its container
        string sError;                                   // why the text was refused
        string sProblem;                                 // why a new key was refused
        int iPos;                                        // its position

        item = Vd_SplitPath(lstPath, out lstParent, out container, out iPos);
        if (item == null)
        {   /* Nothing there. */
            return("That entry no longer exists.");
        }

        if (sText == VoorheesFormat.Vf_RenderItem(this, lstPath))
        {   /* Unchanged text: nothing to do. */
            return(null);
        }

        newItem = VoorheesFormat.Vf_ApplyRawEdit(this, container, item, sText,
            out sError);
        if (newItem == null)
        {   /* Not valid. */
            return(sError);
        }

        if (newItem.Vi_iKind == VoorheesItem.VI_MEMBER)
        {   /* A member: its key must not duplicate another's. */
            sProblem = Vd_RenameProblem(container, iPos, newItem.Vi_sKey);
            if (sProblem != null)
            {   /* Empty or already used. */
                return(sProblem);
            }
        }

        Vd_SetItem(step, lstParent, iPos, item, newItem);

        /* Done. */
        return(null);
    }

    /*------------------------------------------------------------------------*/
    /* Vd_RenameProblem:                                                      */
    /*                                                                        */
    /* Why a member may not take a new key, if it may not: a key the format   */
    /* refuses (Vf_KeyProblem: for JSON an empty key, legal but almost always */
    /* a slip) unless the key is unchanged, or a key already used by ANOTHER  */
    /* member of the container, compared as the format compares keys          */
    /* (Vd_KeyComparer).  Voorhees never CREATES a duplicate key, though it   */
    /* keeps the ones a file already had.                                     */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     container : the member's container.                                */
    /*     iPos      : the member's position.                                 */
    /*     sNewKey   : the proposed key.                                      */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : null when the rename is allowed (or changes nothing),     */
    /*              else the reason, for the user.                            */
    /*------------------------------------------------------------------------*/
    public string Vd_RenameProblem(VoorheesNode container, int iPos,
        string sNewKey)
    {
        VoorheesItem item;                               // the member
        StringComparer comparer;                         // how keys compare here
        string sProblem;                                 // the format's objection to the key
        int i;

        if (container == null || container.Vn_lstItems == null || iPos < 0
            || iPos >= container.Vn_Count()
            || container.Vn_lstItems[iPos].Vi_iKind != VoorheesItem.VI_MEMBER)
        {   /* No member there. */
            return("That member no longer exists.");
        }

        item = container.Vn_lstItems[iPos];
        if (sNewKey == item.Vi_sKey)
        {   /* Unchanged: nothing to refuse. */
            return(null);
        }

        sProblem = VoorheesFormat.Vf_KeyProblem(this, container, item, sNewKey);
        if (sProblem != null)
        {   /* Not a valid key for the format. */
            return(sProblem);
        }

        /* Another member with the same key (the member itself does not      */
        /* count: a format that ignores case may let it change only case).   */
        comparer = Vd_KeyComparer(container);
        for (i = 0; i < container.Vn_lstItems.Count; i++)
        {
            if (i != iPos
                && container.Vn_lstItems[i].Vi_iKind == VoorheesItem.VI_MEMBER
                && comparer.Equals(container.Vn_lstItems[i].Vi_sKey, sNewKey))
            {   /* Already used: renaming would create a duplicate key (said in the format's words). */
                return(VoorheesFormat.Vf_DuplicateMessage(this, container, item,
                    sNewKey));
            }
        }

        /* Allowed. */
        return(null);
    }

    /*------------------------------------------------------------------------*/
    /* Vd_UnusedKey:                                                          */
    /*                                                                        */
    /* A key not yet used in a container, keys compared as its format         */
    /* compares them: the base itself if it is free, else the base followed   */
    /* by 1, 2, 3 ... until one is (Add Child uses the format's new-key base, */
    /* Paste the copied member's key).                                        */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     container : the container.                                         */
    /*     sBase     : the key wanted.                                        */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : a key no member of the container has.                     */
    /*------------------------------------------------------------------------*/
    public string Vd_UnusedKey(VoorheesNode container, string sBase)
    {
        Dictionary<string, int> dictUsed;                // keys already in the container
        string sKey;                                     // candidate key
        int iSuffix;                                     // number tried after sBase

        dictUsed = Vd_KeyCounts(container);
        sKey = sBase;
        iSuffix = 1;
        while (dictUsed.ContainsKey(sKey))
        {
            sKey = sBase + iSuffix.ToString(CultureInfo.InvariantCulture);
            iSuffix++;
        }

        /* Free in this object. */
        return(sKey);
    }

    /*------------------------------------------------------------------------*/
    /* Vd_NextInOrder:                                                        */
    /*                                                                        */
    /* The item after a given one in display order: a container's first item  */
    /* comes straight after it, then its siblings, then its ancestors' later  */
    /* siblings, as in the tree with everything opened.  The empty path (the  */
    /* document) is before everything, so its next is the first item.         */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     lstPath : position path of the current item.                       */
    /*                                                                        */
    /* Returns:                                                               */
    /*     List<int> : the next item's path (a new list), or null when the    */
    /*                 current one is the last in the document.               */
    /*------------------------------------------------------------------------*/
    public List<int> Vd_NextInOrder(List<int> lstPath)
    {
        List<int> lstNext;                               // the path being worked out
        VoorheesNode node;                               // the current item's value, if it has one
        VoorheesNode parent;                             // a container on the way back up
        int iLast;                                       // a position being stepped past

        lstNext = new List<int>(lstPath);
        node = Vd_NodeAt(lstNext);

        if (node != null && node.Vn_Count() > 0)
        {   /* A container with items: its first item is next. */
            lstNext.Add(0);
            return(lstNext);
        }

        /* Otherwise climb until a level has a later sibling. */
        while (lstNext.Count > 0)
        {
            iLast = lstNext[lstNext.Count - 1];
            lstNext.RemoveAt(lstNext.Count - 1);
            parent = Vd_NodeAt(lstNext);

            if (parent != null && iLast + 1 < parent.Vn_Count())
            {   /* A later sibling at this level: that is next. */
                lstNext.Add(iLast + 1);
                return(lstNext);
            }
        }

        /* Climbed past the document: this was the last item. */
        return(null);
    }

    /*------------------------------------------------------------------------*/
    /* Vd_Matches:                                                            */
    /*                                                                        */
    /* Does the item at a path contain the search text?  Looked at:           */
    /*     o with bKeys   : an object member's key (array indexes and the     */
    /*                      top value have no key worth searching);           */
    /*     o with bValues : a plain value's text where the format searches it */
    /*                      (Vf_IsSearchable: JSON null is not, "null" not    */
    /*                      being text the file holds), a comment item's      */
    /*                      text, and the comment on the item's line.         */
    /* Containers are searched through their items.                           */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     lstPath : the item's position path.                                */
    /*     sSearch : text to look for (not empty).                            */
    /*     comp    : exact or case-insensitive comparison.                    */
    /*     bKeys   : test the key.                                            */
    /*     bValues : test the value and comments.                             */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true when any of them contains the text.                    */
    /*------------------------------------------------------------------------*/
    public bool Vd_Matches(List<int> lstPath, string sSearch,
        StringComparison comp, bool bKeys, bool bValues)
    {
        VoorheesItem item;                               // the item
        string sLineComment;                             // the comment on its line, raw

        item = Vd_ItemAt(lstPath);
        if (item == null)
        {   /* Nothing there. */
            return(false);
        }

        if (bKeys && item.Vi_iKind == VoorheesItem.VI_MEMBER
            && item.Vi_sKey.IndexOf(sSearch, comp) >= 0)
        {   /* Object member whose key contains the text. */
            return(true);
        }

        if (!bValues)
        {   /* Keys only: nothing more to test. */
            return(false);
        }

        if (item.Vi_iKind == VoorheesItem.VI_COMMENT)
        {   /* A comment item: its text, without the markers. */
            return(VoorheesFormat.Vf_CommentText(Vd_iFormat, item.Vi_sComment)
                .IndexOf(sSearch, comp) >= 0);
        }

        if (!item.Vi_node.Vn_IsContainer()
            && VoorheesFormat.Vf_IsSearchable(this, item.Vi_node)
            && item.Vi_node.Vn_sText.IndexOf(sSearch, comp) >= 0)
        {   /* Plain value containing the text. */
            return(true);
        }

        sLineComment = item.Vi_LineComment();
        if (sLineComment != null
            && VoorheesFormat.Vf_CommentText(Vd_iFormat, sLineComment).IndexOf(sSearch, comp) >= 0)
        {   /* The comment on its line contains the text. */
            return(true);
        }

        /* Nothing matches. */
        return(false);
    }

    /*------------------------------------------------------------------------*/
    /* Vd_FindNext:                                                           */
    /*                                                                        */
    /* The next item after a given one, in display order and wrapping round,  */
    /* that matches the search (Vd_Matches).  Every item is tried once, the   */
    /* starting one last.  The whole document is searched, opened in the      */
    /* tree or not.                                                           */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     lstStart : position path to start after; null to start at the      */
    /*                first item.                                             */
    /*     sSearch  : text to look for.                                       */
    /*     comp     : exact or case-insensitive comparison.                   */
    /*     bKeys    : look in object member keys.                             */
    /*     bValues  : look in plain values and comments.                      */
    /*                                                                        */
    /* Returns:                                                               */
    /*     List<int> : the matching item's path, or null when nothing         */
    /*                 matches.                                               */
    /*------------------------------------------------------------------------*/
    public List<int> Vd_FindNext(List<int> lstStart, string sSearch,
        StringComparison comp, bool bKeys, bool bValues)
    {
        List<int> lstFirst;                              // the first item in display order
        List<int> lstPath;                               // item being tried
        long lTotal;                                     // items in the document
        long l;

        if (string.IsNullOrEmpty(sSearch) || Vd_root == null)
        {   /* Nothing to look for, or nowhere to look. */
            return(null);
        }

        lstFirst = Vd_NextInOrder(new List<int>());
        if (lstFirst == null)
        {   /* An empty document: nothing to search. */
            return(null);
        }

        /* Every entry but the document node itself. */
        lTotal = Vd_root.Vn_CountEntries() - 1;

        if (lstStart == null || lstStart.Count == 0)
        {   /* No starting item: the first one is tried first. */
            lstPath = lstFirst;
        }
        else
        {   /* Start just after the given item. */
            lstPath = Vd_NextInOrder(lstStart);
            if (lstPath == null)
            {   /* It was the last item: wrap to the first. */
                lstPath = new List<int>(lstFirst);
            }
        }

        /* Every item once, wrapping past the end back to the first. */
        for (l = 0; l < lTotal; l++)
        {
            if (Vd_Matches(lstPath, sSearch, comp, bKeys, bValues))
            {   /* Found. */
                return(lstPath);
            }

            lstPath = Vd_NextInOrder(lstPath);
            if (lstPath == null)
            {   /* Past the last item: back to the first. */
                lstPath = new List<int>(lstFirst);
            }
        }

        /* Searched everything: no match. */
        return(null);
    }

    /*------------------------------------------------------------------------*/
    /* Vd_ReplaceText:                                                        */
    /*                                                                        */
    /* Replaces every occurrence of the search text in a string.  The         */
    /* case-insensitive form uses a Regex with an evaluator that returns the  */
    /* replacement as it is, so a "$" in it is taken literally; passed as an  */
    /* ordinary replacement string, "$0" or "$&" would insert the matched     */
    /* text instead.                                                          */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sText      : string to search.                                     */
    /*     sSearch    : text to replace (never empty here).                   */
    /*     sReplace   : text to put in its place.                             */
    /*     bMatchCase : true for an exact-case match.                         */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : sText with every match replaced.                          */
    /*------------------------------------------------------------------------*/
    public static string Vd_ReplaceText(string sText, string sSearch,
        string sReplace, bool bMatchCase)
    {
        if (bMatchCase)
        {   /* Exact case: String.Replace is an ordinal, literal replace. */
            return(sText.Replace(sSearch, sReplace));
        }

        /* Any case: escape the search text for the Regex, and supply the   */
        /* replacement through an evaluator so it is literal too.           */
        return(Regex.Replace(sText, Regex.Escape(sSearch),
            delegate(Match m) { return(sReplace); },
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
    }

    /*------------------------------------------------------------------------*/
    /* Vd_ReplaceInComment:                                                   */
    /*                                                                        */
    /* Replaces the search text in one comment's TEXT (never its markers) and */
    /* rebuilds the comment in the same style for its place (the format's     */
    /* Vf_RemakeComment: for JSON a line comment whose new text has a line    */
    /* break becomes a block comment).  A comment holding several comments,   */
    /* or one whose new text cannot be a comment (a "*" "/" inside a block    */
    /* comment), is refused.                                                  */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     container    : the container of the item the comment belongs to.   */
    /*     iPos         : that item's position.                               */
    /*     bLineComment : true for the item's line comment, false when the    */
    /*                    item is the comment entry itself.                   */
    /*     sRaw         : the comment, markers included.                      */
    /*     sSearch      : text to replace.                                    */
    /*     sReplace     : text to put in its place.                           */
    /*     bMatchCase   : true for an exact-case match.                       */
    /*     comp         : the matching comparison, for the quick test.        */
    /*     sNewRaw      : set to the new comment, or to sRaw when unchanged.  */
    /*                                                                        */
    /* Returns:                                                               */
    /*     int : 1 when replaced, 0 when there was no match, -1 when refused. */
    /*------------------------------------------------------------------------*/
    int Vd_ReplaceInComment(VoorheesNode container, int iPos,
        bool bLineComment, string sRaw, string sSearch,
        string sReplace, bool bMatchCase, StringComparison comp,
        out string sNewRaw)
    {
        string sText;                                    // the comment's text
        string sError;                                   // why a comment could not be rebuilt

        sNewRaw = sRaw;
        sText = VoorheesFormat.Vf_CommentText(Vd_iFormat, sRaw);
        if (sText.IndexOf(sSearch, comp) < 0)
        {   /* Nothing to replace. */
            return(0);
        }

        if (!VoorheesFormat.Vf_IsSingleComment(Vd_iFormat, sRaw))
        {   /* Several comments in one: editable only through Raw. */
            return(-1);
        }

        sNewRaw = VoorheesFormat.Vf_RemakeComment(this, container, iPos, sRaw,
            Vd_ReplaceText(sText, sSearch, sReplace, bMatchCase), bLineComment,
            out sError);
        if (sNewRaw == null)
        {   /* The result cannot be written as a comment. */
            sNewRaw = sRaw;
            return(-1);
        }

        /* Replaced. */
        return(1);
    }

    /*------------------------------------------------------------------------*/
    /* Vd_ReplaceAt:                                                          */
    /*                                                                        */
    /* Replaces the search text in one item, recording at most one SETITEM    */
    /* change in a step:                                                      */
    /*     o with bValues, in a plain value the format searches               */
    /*       (Vf_IsSearchable: never JSON null): it keeps its kind            */
    /*       (Vf_ConvertText), so a string stays a string even if the result  */
    /*       looks like a number, and a number or boolean whose result is no  */
    /*       longer valid is SKIPPED and counted;                             */
    /*     o with bValues, in a comment item's text and in the comment on the */
    /*       item's line (Vd_ReplaceInComment; refusals skipped and counted); */
    /*     o with bKeys, in an object member's key: a result that is empty or */
    /*       already used in the object is SKIPPED and counted, so no         */
    /*       duplicate key is ever created.                                   */
    /* Nothing moves, so positions elsewhere stay valid.                      */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     step       : the step to record the change in.                     */
    /*     lstPath    : the item's position path.                             */
    /*     sSearch    : text to replace.                                      */
    /*     sReplace   : text to put in its place.                             */
    /*     bMatchCase : true for an exact-case match.                         */
    /*     comp       : the matching comparison, for the quick tests.         */
    /*     bKeys      : replace in the key.                                   */
    /*     bValues    : replace in the value and comments.                    */
    /*     iSkipped   : incremented for each match refused.                   */
    /*                                                                        */
    /* Returns:                                                               */
    /*     int : replacements made here (one per field changed).              */
    /*------------------------------------------------------------------------*/
    public int Vd_ReplaceAt(VoorheesEditStep step, List<int> lstPath,
        string sSearch, string sReplace, bool bMatchCase,
        StringComparison comp, bool bKeys, bool bValues, ref int iSkipped)
    {
        VoorheesItem item;                               // the item as it is
        VoorheesItem newItem;                            // the item being built
        VoorheesNode container;                          // its container
        VoorheesNode newNode;                            // a replacement value
        List<int> lstParent;                             // the container's path
        string sNewText;                                 // value text after replacing
        string sNewKey;                                  // key after replacing
        string sNewRaw;                                  // a comment after replacing
        string sLineComment;                             // the comment on the item's line
        int iPos;                                        // the item's position
        int iResult;                                     // outcome of a comment replace
        int iCount;                                      // replacements made

        iCount = 0;
        item = Vd_ItemAt(lstPath);
        if (item == null)
        {   /* Nothing there. */
            return(0);
        }

        lstParent = lstPath.GetRange(0, lstPath.Count - 1);
        container = Vd_NodeAt(lstParent);
        iPos = lstPath[lstPath.Count - 1];
        newItem = item;

        if (bValues && item.Vi_iKind == VoorheesItem.VI_COMMENT)
        {   /* A comment item: replace in its text. */
            iResult = Vd_ReplaceInComment(container, iPos, false,
                item.Vi_sComment, sSearch, sReplace,
                bMatchCase, comp, out sNewRaw);
            if (iResult < 0)
            {   /* Refused: skip and count it. */
                iSkipped++;
            }
            else if (iResult > 0 && sNewRaw != item.Vi_sComment)
            {   /* Changed. */
                newItem = newItem.Vi_WithComment(sNewRaw);
                iCount++;
            }
        }

        if (bValues && item.Vi_IsValue() && !item.Vi_node.Vn_IsContainer()
            && VoorheesFormat.Vf_IsSearchable(this, item.Vi_node)
            && item.Vi_node.Vn_sText.IndexOf(sSearch, comp) >= 0)
        {   /* A plain value containing the text: replace in it, keeping its kind. */
            sNewText = Vd_ReplaceText(item.Vi_node.Vn_sText, sSearch, sReplace,
                bMatchCase);
            newNode = VoorheesFormat.Vf_ConvertText(this, item.Vi_node, sNewText);
            if (newNode == null)
            {   /* Not valid for the value's kind: skip and count it. */
                iSkipped++;
            }
            else if (!newNode.Vn_SameAs(item.Vi_node))
            {   /* A real change. */
                newItem = newItem.Vi_WithNode(newNode);
                iCount++;
            }
        }

        sLineComment = newItem.Vi_LineComment();
        if (bValues && item.Vi_IsValue() && sLineComment != null)
        {   /* The comment on the item's line. */
            iResult = Vd_ReplaceInComment(container, iPos, true,
                sLineComment, sSearch, sReplace,
                bMatchCase, comp, out sNewRaw);
            if (iResult < 0)
            {   /* Refused: skip and count it. */
                iSkipped++;
            }
            else if (iResult > 0 && sNewRaw != sLineComment)
            {   /* Changed. */
                newItem = newItem.Vi_WithLineComment(sNewRaw);
                iCount++;
            }
        }

        if (bKeys && item.Vi_iKind == VoorheesItem.VI_MEMBER
            && item.Vi_sKey.IndexOf(sSearch, comp) >= 0)
        {   /* An object member whose key contains the text. */
            sNewKey = Vd_ReplaceText(item.Vi_sKey, sSearch, sReplace,
                bMatchCase);
            if (sNewKey != item.Vi_sKey)
            {   /* It would change: allowed only to an unused, non-empty key. */
                if (Vd_RenameProblem(container, iPos, sNewKey) != null)
                {   /* Empty or already used: skip and count it. */
                    iSkipped++;
                }
                else
                {   /* Rename it. */
                    newItem = newItem.Vi_WithKey(sNewKey);
                    iCount++;
                }
            }
        }

        if (!ReferenceEquals(newItem, item))
        {   /* Something changed: one change for the whole item. */
            Vd_Apply(step, VoorheesChange.Vch_NewSetItem(lstParent, iPos,
                item, newItem));
        }

        /* How many replacements were made here. */
        return(iCount);
    }

    /*------------------------------------------------------------------------*/
    /* Vd_ReplaceAll:                                                         */
    /*                                                                        */
    /* Replaces throughout the document, recording every change in one step.  */
    /* Every item is visited once in display order; replacing never adds or   */
    /* removes items, so the walk is not disturbed by what it changes, and    */
    /* every copy of a duplicated key is visited.                             */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     step       : the step to record changes in.                        */
    /*     sSearch    : text to replace.                                      */
    /*     sReplace   : text to put in its place.                             */
    /*     bMatchCase : true for an exact-case match.                         */
    /*     bKeys      : replace in object member keys.                        */
    /*     bValues    : replace in plain values and comments.                 */
    /*     iSkipped   : set to the number of matches refused.                 */
    /*                                                                        */
    /* Returns:                                                               */
    /*     int : replacements made.                                           */
    /*------------------------------------------------------------------------*/
    public int Vd_ReplaceAll(VoorheesEditStep step, string sSearch,
        string sReplace, bool bMatchCase, bool bKeys, bool bValues,
        out int iSkipped)
    {
        StringComparison comp;                           // exact or case-insensitive comparison
        List<int> lstPath;                               // item being visited
        int iCount;                                      // replacements made

        iSkipped = 0;
        iCount = 0;
        if (string.IsNullOrEmpty(sSearch) || Vd_root == null)
        {   /* Nothing to look for, or nowhere to look. */
            return(0);
        }

        if (bMatchCase)
        {   /* Exact case. */
            comp = StringComparison.Ordinal;
        }
        else
        {   /* Any case. */
            comp = StringComparison.OrdinalIgnoreCase;
        }

        /* Every item, first to last. */
        lstPath = Vd_NextInOrder(new List<int>());
        while (lstPath != null)
        {
            iCount += Vd_ReplaceAt(step, lstPath, sSearch, sReplace,
                bMatchCase, comp, bKeys, bValues, ref iSkipped);
            lstPath = Vd_NextInOrder(lstPath);
        }

        /* Total replacements. */
        return(iCount);
    }
}
