/*----------------------------------------------------------------------------*/
/* VoorheesTreeView.cs                                                        */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/*----------------------------------------------------------------------------*/
/* The editor's tree: a picture of a VoorheesDocument that is built LAZILY    */
/* and kept up to date IN PLACE.                                              */
/*                                                                            */
/*     o One tree node per item, at the same position: tree child index ==    */
/*       item position at every level, and the top level is the document's    */
/*       items (top-level comments and the top value).  So a tree node's      */
/*       position path (its indexes, up to the top) IS the item's path in     */
/*       the document.                                                        */
/*     o A container's children are created only when it is first expanded.   */
/*       Until then it holds one placeholder child (Tag null) so it shows an  */
/*       expand box.  Opening the 40,000-item array of a 9 MB file therefore  */
/*       costs one level, not the whole document.                             */
/*     o Each change the editor makes to the document (VoorheesChange) is     */
/*       applied to the tree by Vtv_ApplyChange: one node relabelled,         */
/*       inserted or removed, plus the siblings whose labels depend on it.    */
/*       Nothing is ever rebuilt wholesale.                                   */
/*                                                                            */
/* Labels: a member "key: value", {key} for an object, [key] for an array     */
/* (array elements use their index as the key, the top value "root"); a       */
/* comment entry "comment: words".  A key that appears more than once in its  */
/* object is drawn in VTV_COLORDUPLICATE with "(duplicate)" after it.  The    */
/* comment on an item's line follows its label, and a marker follows an item  */
/* with comments only the Raw box can edit; both, and comment entries, are    */
/* drawn in the muted (grey) colour.  With the Show Raw Text option on, every */
/* label is instead the first line of the item's text exactly as in the file. */
/*                                                                            */
/* Drawing: the control draws every label itself, natively, in the node's     */
/* own colour (TreeNode.ForeColor: red for a duplicate, grey for a comment    */
/* entry).  Only a label with a grey TAIL (the line comment or the marker     */
/* after a value) needs more: after the control has drawn such an item, the   */
/* tail is painted over in grey (custom draw, item post-paint, handled in     */
/* WndProc).  WinForms' own owner-drawn mode was tried first and made         */
/* inserting nodes three to four times slower (40,000 children: about 4 s     */
/* against 1 s), because every label then went through managed drawing.       */
/*                                                                            */
/* Progress: filling a big container and removing a big branch each take a    */
/* long time inside one call into the control.  The control counts the items  */
/* it inserts (TVM_INSERTITEM) and deletes (TVN_DELETEITEM) in WndProc and    */
/* moves a progress bar from there; the window is put up BEFORE such a call   */
/* when the measured rates say it will be slow, never from inside it.         */
/*                                                                            */
/* Classes:                                                                   */
/*     VoorheesTreeTag  (Vtt_) : stored in every real tree node's Tag.        */
/*     VoorheesTreeView (Vtv_) : the control.                                 */
/*----------------------------------------------------------------------------*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Voorhees.Forms;

/*----------------------------------------------------------------------------*/
/* VoorheesTreeTag                                                            */
/*                                                                            */
/* What the tree needs to remember about one node beyond its text.  The       */
/* node's place in the document is NOT stored: it is the node's position      */
/* path, which stays right as siblings come and go.                           */
/*----------------------------------------------------------------------------*/
class VoorheesTreeTag
{
    public bool Vtt_bPopulated;                          // its children have been created (always true for a non-container)
    public int Vtt_iMutedFrom;                           // where the grey tail of the label starts; -1 for none (a label grey throughout uses TreeNode.ForeColor)
    public int Vtt_iValueFrom;                           // where the editable part of the label starts ("key: " ends); 0 when the whole label is the text
    public int Vtt_iKeyFrom;                             // where the key (or index, or "root") starts in the label; -1 when there is none
    public int Vtt_iKeyLength;                           // how long it is
    public int Vtt_iValueLength;                         // how long the shown value is, from Vtt_iValueFrom; 0 when the label shows no value (containers, comments, an INI key alone, the raw view)
    public int Vtt_iValueRole;                           // the shown value's colour role (VoorheesFormat.VF_ROLE_); -1 when there is no value

    /*------------------------------------------------------------------------*/
    /* Vtt_Create:                                                            */
    /*                                                                        */
    /* A tag.                                                                 */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     bPopulated : the node's children exist (or it can have none).      */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesTreeTag : the tag, with no grey tail, key or value run.    */
    /*------------------------------------------------------------------------*/
    public static VoorheesTreeTag Vtt_Create(bool bPopulated)
    {
        VoorheesTreeTag tag;                             // the new tag

        tag = new VoorheesTreeTag();
        tag.Vtt_bPopulated = bPopulated;
        tag.Vtt_iMutedFrom = -1;
        tag.Vtt_iValueFrom = 0;
        tag.Vtt_iKeyFrom = -1;
        tag.Vtt_iKeyLength = 0;
        tag.Vtt_iValueLength = 0;
        tag.Vtt_iValueRole = -1;

        /* Ready; the label code sets the tail, the key and the value's run. */
        return(tag);
    }
}

/*----------------------------------------------------------------------------*/
/* VoorheesTreeView                                                           */
/*                                                                            */
/* The control.  The editor gives it the document (Vtv_SetDocument), tells    */
/* it about every change (Vtv_ApplyChange) and selects by path (Vtv_Select).  */
/* While it rearranges nodes itself Vtv_IsBusy is true, during which the      */
/* editor ignores selection events.  The class is partial only so the test    */
/* harness (tools\VoorheesHarness.cs, its own exe) can add a check that       */
/* relabels nodes the way a fill does; nothing of that is in Voorhees.exe.    */
/*----------------------------------------------------------------------------*/
partial class VoorheesTreeView : TreeView
{
    /*------------------------------------------------------------------------*/
    /* TreeView messages and notifications (commctrl.h).  TV_FIRST is 0x1100  */
    /* and TVN_FIRST is -400.                                                 */
    /*------------------------------------------------------------------------*/

    const int VTV_TVM_INSERTITEMA = 0x1100;              // TV_FIRST + 0: insert an item, ANSI form
    const int VTV_TVM_INSERTITEMW = 0x1132;              // TV_FIRST + 50: insert an item, Unicode form (WinForms')
    const int VTV_TVM_GETITEMA = 0x110C;                 // TV_FIRST + 12: read an item's state, ANSI form
    const int VTV_TVM_GETITEMW = 0x113E;                 // TV_FIRST + 62: read an item's state, Unicode form (WinForms')
    const int VTV_TVM_GETEDITCONTROL = 0x110F;           // TV_FIRST + 15: the edit box of the in-place edit
    const int VTV_WM_SETTEXT = 0x000C;                   // set a window's (the edit box's) text
    const int VTV_EM_SETSEL = 0x00B1;                    // edit box: select characters wParam..lParam (0..-1: all)
    const int VTV_WM_REFLECT_NOTIFY = 0x204E;            // WM_REFLECT (0x2000) + WM_NOTIFY: a notification WinForms bounces back to the control
    const int VTV_TVN_DELETEITEMA = -409;                // TVN_FIRST - 9: an item was deleted, ANSI form
    const int VTV_TVN_DELETEITEMW = -458;                // TVN_FIRST - 58: an item was deleted, Unicode form (seen in practice)

    /*------------------------------------------------------------------------*/
    /* Custom draw (commctrl.h): the control asks, through a reflected        */
    /* NM_CUSTOMDRAW notification, before and after it paints each item.      */
    /* WinForms answers the pre-paint stages (it applies TreeNode colours);   */
    /* this control adds a post-paint request and draws the grey tails then.  */
    /*------------------------------------------------------------------------*/

    const int VTV_NM_CUSTOMDRAW = -12;                   // NM_FIRST - 12: the custom draw notification
    const int VTV_CDDS_ITEMPREPAINT = 0x00010001;        // CDDS_ITEM | CDDS_PREPAINT: an item is about to be painted
    const int VTV_CDDS_ITEMPOSTPAINT = 0x00010002;       // CDDS_ITEM | CDDS_POSTPAINT: an item has just been painted
    const int VTV_CDRF_NOTIFYPOSTPAINT = 0x00000010;     // answer to a pre-paint: send the post-paint stage too
    const int VTV_CDIS_SELECTED = 0x0001;                // item state: the item is selected

    /*------------------------------------------------------------------------*/
    /* Label limits and markers.  Non-ASCII marks are built from their code   */
    /* points, which keeps this file pure ASCII.                              */
    /*------------------------------------------------------------------------*/

    public const int VTV_LEAFMAX = 60;                                   // longest value (or raw line) shown in a label, characters
    const string VTV_DUPLICATEMARK = "  (duplicate)";                    // after a key that appears more than once in its object
    const string VTV_ODDMARK = "  /*...*/";                              // after an item with comments only the Raw box can edit
    const string VTV_COMMENTPREFIX = "comment: ";                        // start of a comment entry's label
    static readonly string VTV_RETURNMARK = ((char)0x21B5).ToString();   // U+21B5 return arrow: a line break in a label
    static readonly Color VTV_COLORDUPLICATE = Color.Firebrick;          // repeated keys
    static readonly Color VTV_COLORMUTED = SystemColors.GrayText;        // comments and markers
    public static readonly Color VTV_COLORKEY = Color.DarkBlue;          // keys, indexes and "root", with View > Color Keys on

    /*------------------------------------------------------------------------*/
    /* Name colour roles: which kind of name a                                */
    /* label's name part is.  Value roles are VoorheesFormat.VF_ROLE_.        */
    /*------------------------------------------------------------------------*/

    public const int VTV_NAME_TOP = 0;                   // a top-level container's name: [section], [include ...], REG keys, JSON top-level keys
    public const int VTV_NAME_NESTED = 1;                // a container inside another (JSON objects / arrays below the top, sections in an auto-saved block)
    public const int VTV_NAME_ENTRY = 2;                 // a plain value's key or index
    public const int VTV_NAMEROLECOUNT = 3;              // how many name roles there are

    /*------------------------------------------------------------------------*/
    /* Progress: nodes counted between reports, and the measured costs used   */
    /* to decide beforehand whether a populate or removal will be slow        */
    /* enough to put the progress window up (9 MB, 440,000-value document on  */
    /* the development machine).                                              */
    /*------------------------------------------------------------------------*/

    const long VTV_PROGRESSSTEP = 2000;                  // nodes per progress report
    const long VTV_FILLUSPERNODE = 16;                   // cost of labelling and inserting one node, microseconds
    const long VTV_CLEARUSPERNODE = 64;                  // cost of removing one node from the control, microseconds
    const int VTV_SHOWDELAYMS = 300;                     // work shorter than this never shows the progress window

    public VoorheesDocument Vtv_doc;                     // the document shown
    public bool Vtv_bShowRaw;                            // labels are the items' raw text
    public bool Vtv_bColorKeys;                          // names and values are drawn in their roles' colours (View > Color Keys)
    public Color[] Vtv_arrNameColors;                    // with Color Keys on: each VTV_NAME_ role's colour
    public Color[] Vtv_arrValueColors;                   // with Color Keys on: each VF_ROLE_ value role's colour
    int Vtv_iBusyDepth;                                  // above 0 while the tree rearranges itself (nests): see Vtv_IsBusy
    string Vtv_sEditValue;                               // the full text for a prepared in-place edit, else null (Vtv_PrepareValueEdit)
    int Vtv_iEditShift;                                  // how far right its edit box goes: the key part's width, pixels
    VoorheesTreeEditShifter Vtv_editShifter;             // watches that edit box while it is open, else null
    TreeNode Vtv_editNode;                               // the node whose key part is drawn beside the open box, else null

    BusyModalForm Vtv_prog;                              // progress window while counting, else null
    long Vtv_lDone;                                      // progress units done so far
    long Vtv_lTotal;                                     // progress units in all
    bool Vtv_bCountInserts;                              // count inserted items
    bool Vtv_bCountDeletes;                              // count deleted items (and answer their notifications)

    /*------------------------------------------------------------------------*/
    /* Vtv_Create:                                                            */
    /*                                                                        */
    /* The control, set up to draw its own label text.                        */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesTreeView : the control, with no document yet.              */
    /*------------------------------------------------------------------------*/
    public static VoorheesTreeView Vtv_Create()
    {
        VoorheesTreeView tree;                           // the new control

        /* Normal (native) drawing: see the file header for why. */
        tree = new VoorheesTreeView();
        tree.DrawMode = TreeViewDrawMode.Normal;
        tree.Vtv_doc = null;
        tree.Vtv_bShowRaw = false;
        tree.Vtv_bColorKeys = false;
        tree.Vtv_iBusyDepth = 0;
        tree.Vtv_arrNameColors = Vtv_DefaultNameColors();
        tree.Vtv_arrValueColors = Vtv_DefaultValueColors();

        /* Ready for a document. */
        return(tree);
    }

    /*------------------------------------------------------------------------*/
    /* Vtv_DefaultNameColors:                                                 */
    /*                                                                        */
    /* The name roles' default colours: dark                                  */
    /* blue for all three, as View > Color Keys has always drawn keys.        */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     Color[] : one colour per VTV_NAME_ role (a new array).             */
    /*------------------------------------------------------------------------*/
    public static Color[] Vtv_DefaultNameColors()
    {
        Color[] arrColors;                               // the defaults
        int i;

        arrColors = new Color[VTV_NAMEROLECOUNT];
        for (i = 0; i < arrColors.Length; i++)
        {
            arrColors[i] = VTV_COLORKEY;
        }

        /* Every name dark blue. */
        return(arrColors);
    }

    /*------------------------------------------------------------------------*/
    /* Vtv_DefaultValueColors:                                                */
    /*                                                                        */
    /* The value roles' default colours (decision C3): black, the control's   */
    /* own text colour, for every type.                                       */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     Color[] : one colour per VoorheesFormat.VF_ROLE_ (a new array).    */
    /*------------------------------------------------------------------------*/
    public static Color[] Vtv_DefaultValueColors()
    {
        Color[] arrColors;                               // the defaults
        int i;

        arrColors = new Color[VoorheesFormat.VF_ROLECOUNT];
        for (i = 0; i < arrColors.Length; i++)
        {
            arrColors[i] = SystemColors.WindowText;
        }

        /* Every value black. */
        return(arrColors);
    }

    /*------------------------------------------------------------------------*/
    /* Vtv_IsBusy:                                                            */
    /*                                                                        */
    /* Whether the tree is rearranging its own nodes right now, so a          */
    /* selection event comes from that and not from the user: the editor      */
    /* ignores selection events while this is true.                           */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true inside any of the tree's own rearrangements.           */
    /*------------------------------------------------------------------------*/
    public bool Vtv_IsBusy()
    {
        /* Rearrangements nest, hence a depth rather than a flag. */
        return(Vtv_iBusyDepth > 0);
    }

    /*------------------------------------------------------------------------*/
    /* Vtv_SetDocument:                                                       */
    /*                                                                        */
    /* Shows a new document: the old nodes are removed (counted, with the     */
    /* progress window put up first if there are enough of them to be slow),  */
    /* the top level is created (the document's items; containers             */
    /* unpopulated), and the entry the format names (Vd_InitialPath: for      */
    /* JSON the top value) is selected, opened one level if the format says   */
    /* so.  An empty document selects nothing.                                */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc : the document.                                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the tree shows the document's top level.                    */
    /*------------------------------------------------------------------------*/
    public void Vtv_SetDocument(VoorheesDocument doc)
    {
        List<int> lstFirst;                              // the entry to select first
        TreeNode[] arrNodes;                             // the new top-level nodes
        Dictionary<string, int> dictKeys;                // the document's own key counts (none in JSON)
        bool bExpand;                                    // open that entry
        int i;

        Vtv_iBusyDepth++;
        try
        {
            Vtv_RemoveNodesCounted(Nodes, -1);
            Vtv_doc = doc;

            /* The top level: one node per document item. */
            dictKeys = doc.Vd_KeyCounts(doc.Vd_root);
            arrNodes = new TreeNode[doc.Vd_root.Vn_Count()];
            for (i = 0; i < arrNodes.Length; i++)
            {
                arrNodes[i] = Vtv_MakeNode(doc.Vd_root, new List<int>(), i, dictKeys, -1);
            }

            BeginUpdate();
            Nodes.AddRange(arrNodes);
            EndUpdate();
        }
        finally
        {
            Vtv_iBusyDepth--;
        }

        /* The format's first entry: opened one level if it says so, and   */
        /* selected.                                                       */
        lstFirst = doc.Vd_InitialPath(out bExpand);
        if (lstFirst == null)
        {   /* An empty document: nothing to select. */
            return;
        }

        if (bExpand)
        {   /* JSON's top value: opened one level. */
            Vtv_ExpandPath(lstFirst);
        }
        Vtv_Select(lstFirst);
    }

    /*------------------------------------------------------------------------*/
    /* Vtv_RemoveNodesCounted:                                                */
    /*                                                                        */
    /* Removes one node (or every node of a collection) from the control,     */
    /* with progress when it is big: two units per tree node removed (the     */
    /* control reads each once and deletes each once), the window put up      */
    /* first when VTV_CLEARUSPERNODE says it will take longer than the show   */
    /* delay.                                                                 */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     coll  : the collection the node is in.                             */
    /*     iPos  : the node's position; -1 to clear the whole collection.     */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the node (or every node) is gone.                           */
    /*------------------------------------------------------------------------*/
    void Vtv_RemoveNodesCounted(TreeNodeCollection coll, int iPos)
    {
        long lNodes;                                     // tree nodes going
        BusyModalForm prog;                              // progress window, shown only if this is slow
        int i;

        /* How many tree nodes are going (only populated ones exist). */
        lNodes = 0;
        if (iPos < 0)
        {   /* The whole collection. */
            for (i = 0; i < coll.Count; i++)
            {
                lNodes += 1 + coll[i].GetNodeCount(true);
            }
        }
        else
        {   /* One node and its branch. */
            lNodes = 1 + coll[iPos].GetNodeCount(true);
        }

        if (lNodes * VTV_CLEARUSPERNODE / 1000 < VTV_SHOWDELAYMS)
        {   /* Quick: no progress needed. */
            Vtv_RemoveNodesNow(coll, iPos);
            return;
        }

        /* Slow: count the work, with the window up before it starts. */
        prog = BusyModalForm.BMF_BeginUiProgress(FindForm(), "Updating the tree",
            Vtv_DocName(), "Voorhees", VTV_SHOWDELAYMS);
        try
        {
            prog.BMF_ShowNow();
            Vtv_prog = prog;
            Vtv_lDone = 0;
            Vtv_lTotal = lNodes * 2;
            Vtv_bCountDeletes = true;
            BeginUpdate();
            Vtv_RemoveNodesNow(coll, iPos);
            EndUpdate();
        }
        finally
        {
            Vtv_bCountDeletes = false;
            Vtv_prog = null;
            prog.BMF_EndUiProgress();
        }
    }

    /*------------------------------------------------------------------------*/
    /* Vtv_RemoveNodesNow:                                                    */
    /*                                                                        */
    /* Removes one node, or clears a collection.                              */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     coll : the collection.                                             */
    /*     iPos : the node's position; -1 to clear the collection.            */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the node or nodes are gone.                                 */
    /*------------------------------------------------------------------------*/
    static void Vtv_RemoveNodesNow(TreeNodeCollection coll, int iPos)
    {
        if (iPos < 0)
        {   /* Everything. */
            coll.Clear();
        }
        else
        {   /* The one node. */
            coll.RemoveAt(iPos);
        }
    }

    /*------------------------------------------------------------------------*/
    /* Vtv_DocName:                                                           */
    /*                                                                        */
    /* The document's file name, for the progress window.                     */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the name, or "Untitled".                                  */
    /*------------------------------------------------------------------------*/
    string Vtv_DocName()
    {
        if (Vtv_doc == null || Vtv_doc.Vd_sPath == null)
        {   /* Never saved. */
            return("Untitled");
        }

        /* The file's name without its folder. */
        return(System.IO.Path.GetFileName(Vtv_doc.Vd_sPath));
    }

    /*------------------------------------------------------------------------*/
    /* Vtv_MakeNode:                                                          */
    /*                                                                        */
    /* A new, unpopulated tree node for one item: labelled and coloured, with */
    /* a placeholder child if it is a container with items.                   */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     container     : the item's container.                              */
    /*     lstContainer  : the container's path.                              */
    /*     iPos          : the item's position.                               */
    /*     dictKeyCounts : the container's key counts (Vn_KeyCounts).         */
    /*     iElementIndex : an array element's index if the caller is counting */
    /*                     them as it goes; -1 to have it counted here.       */
    /*                                                                        */
    /* Returns:                                                               */
    /*     TreeNode : the node, not in any tree yet.                          */
    /*------------------------------------------------------------------------*/
    TreeNode Vtv_MakeNode(VoorheesNode container, List<int> lstContainer,
        int iPos, Dictionary<string, int> dictKeyCounts, int iElementIndex)
    {
        TreeNode treeNode;                               // the node
        VoorheesItem item;                               // its item
        VoorheesTreeTag tag;                             // its tag
        bool bHasChildren;                               // it is a container with items

        item = container.Vn_lstItems[iPos];
        bHasChildren = item.Vi_IsValue() && item.Vi_node.Vn_IsContainer()
            && item.Vi_node.Vn_Count() > 0;

        tag = VoorheesTreeTag.Vtt_Create(!bHasChildren);
        treeNode = new TreeNode();
        treeNode.Tag = tag;
        Vtv_SetLabel(treeNode, container, lstContainer, iPos, dictKeyCounts,
            iElementIndex);

        if (bHasChildren)
        {   /* Not populated yet: a placeholder gives it an expand box. */
            treeNode.Nodes.Add(new TreeNode());
        }

        /* Labelled, ready to insert. */
        return(treeNode);
    }

    /*------------------------------------------------------------------------*/
    /* Vtv_SetLabel:                                                          */
    /*                                                                        */
    /* Works out an item's label and colours (see the file header) and puts   */
    /* them on its tree node, changing the node's text and colour only if     */
    /* they differ (each costs a message to the control).                     */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     treeNode      : the node.                                          */
    /*     container     : the item's container.                              */
    /*     lstContainer  : the container's path.                              */
    /*     iPos          : the item's position.                               */
    /*     dictKeyCounts : the container's key counts.                        */
    /*     iElementIndex : an array element's index if the caller is counting */
    /*                     them as it goes (a loop over every item must, or   */
    /*                     labelling n elements costs n squared); -1 to have  */
    /*                     it counted here, for a single label.               */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the node's text and colour are set, and its tag says where  */
    /*            the grey tail, the key and the value (with its colour       */
    /*            role) are in the label.                                     */
    /*------------------------------------------------------------------------*/
    void Vtv_SetLabel(TreeNode treeNode, VoorheesNode container,
        List<int> lstContainer, int iPos, Dictionary<string, int> dictKeyCounts,
        int iElementIndex)
    {
        VoorheesItem item;                               // the item
        VoorheesTreeTag tag;                             // the node's tag
        string sLabel;                                   // the label being built
        string sName;                                    // key, index or "root"
        string sOpen;                                    // a container label's opening bracket
        string sClose;                                   // its closing bracket
        string sSeparator;                               // between a plain value's name and value
        string sValue;                                   // a plain value as its label shows it, or null for none
        string sLineComment;                             // the comment on its line
        string sRaw;                                     // raw text, for the raw option
        List<int> lstPath;                               // the item's path, for the raw option
        bool bTruncated;                                 // the raw text was cut
        int iCount;                                      // copies of the member's key
        int iMutedFrom;                                  // where the grey tail starts
        int iValueFrom;                                  // where the editable value starts
        int iKeyFrom;                                    // where the key (name) starts, -1 for none
        int iKeyLength;                                  // its length
        int iValueLength;                                // how long the shown value is, 0 for none
        int iValueRole;                                  // its colour role (VF_ROLE_), -1 for none
        int iBreak;                                      // first line break in the raw text
        Color colorMain;                                 // colour of the label (all of it but the tail)

        item = container.Vn_lstItems[iPos];
        tag = (VoorheesTreeTag)treeNode.Tag;
        iMutedFrom = -1;
        iValueFrom = 0;
        iKeyFrom = -1;
        iKeyLength = 0;
        iValueLength = 0;
        iValueRole = -1;
        colorMain = Color.Empty;

        if (item.Vi_iKind == VoorheesItem.VI_MEMBER
            && dictKeyCounts.TryGetValue(item.Vi_sKey, out iCount) && iCount > 1)
        {   /* A repeated key: highlighted. */
            colorMain = VTV_COLORDUPLICATE;
        }

        if (Vtv_bShowRaw)
        {   /* Raw option: the first line of the item's text, as in the file. */
            lstPath = new List<int>(lstContainer);
            lstPath.Add(iPos);
            sRaw = VoorheesFormat.Vf_RenderItemLimited(Vtv_doc, lstPath,
                VTV_LEAFMAX * 4, out bTruncated);
            iBreak = sRaw.IndexOfAny(new char[] { '\r', '\n' });
            if (iBreak >= 0)
            {   /* Several lines: the first one. */
                sRaw = sRaw.Substring(0, iBreak);
            }
            sLabel = Vtv_OneLine(sRaw);

            if (item.Vi_iKind == VoorheesItem.VI_COMMENT)
            {   /* A comment entry: all grey, drawn so by the control itself. */
                colorMain = VTV_COLORMUTED;
            }
        }
        else if (item.Vi_iKind == VoorheesItem.VI_COMMENT)
        {   /* A comment entry: its words, all grey, drawn so by the control itself. */
            sLabel = VTV_COMMENTPREFIX + Vtv_OneLine(
                VoorheesFormat.Vf_CommentText(Vtv_doc.Vd_iFormat, item.Vi_sComment));
            iValueFrom = VTV_COMMENTPREFIX.Length;
            colorMain = VTV_COLORMUTED;
        }
        else
        {   /* A value: name, then value or brackets, as its format labels them.   */
            /* The name: key, index or root (JSON), counted by the format if       */
            /* the caller is not counting element indexes.                         */
            sName = Vtv_OneLine(VoorheesFormat.Vf_ItemName(Vtv_doc, container,
                iPos, iElementIndex));

            /* The name is the key part (View > Color Keys colours it):   */
            /* inside the brackets of a container, first for a value.     */
            iKeyLength = sName.Length;
            if (VoorheesFormat.Vf_LabelBrackets(Vtv_doc.Vd_iFormat,
                item.Vi_node.Vn_iKind, out sOpen, out sClose))
            {   /* A container: its brackets round the name (JSON {name}, [name]). */
                sLabel = sOpen + sName + sClose;
                iKeyFrom = sOpen.Length;
            }
            else
            {   /* Plain value: name, separator, value (the value is what an in-place edit changes). */
                sValue = VoorheesFormat.Vf_LabelValue(Vtv_doc, item.Vi_node);
                if (sValue == null)
                {   /* No value to show (an INI key on its own): the name alone. */
                    sLabel = sName;
                    iValueFrom = sName.Length;
                }
                else
                {   /* Name, separator, value.  The value is a run of its own   */
                    /* for View > Color Keys, coloured by the role its format   */
                    /* gives its kind (text,                                    */
                    /* number, boolean, null, binary).                          */
                    sSeparator = VoorheesFormat.Vf_LabelSeparator(Vtv_doc.Vd_iFormat);
                    sValue = Vtv_OneLine(sValue);
                    sLabel = sName + sSeparator + sValue;
                    iValueFrom = sName.Length + sSeparator.Length;
                    iValueLength = sValue.Length;
                    iValueRole = VoorheesFormat.Vf_ValueRole(item.Vi_node.Vn_iKind);
                }
                iKeyFrom = 0;
            }

            if (colorMain == VTV_COLORDUPLICATE)
            {   /* Say so in words too. */
                sLabel += VTV_DUPLICATEMARK;
            }

            /* Comments after it, in grey. */
            sLineComment = item.Vi_LineComment();
            if (sLineComment != null)
            {   /* The comment on its line, as its format shows it (JSON "// words"). */
                iMutedFrom = sLabel.Length;
                sLabel += "  " + Vtv_OneLine(VoorheesFormat.Vf_LabelLineComment(
                    Vtv_doc.Vd_iFormat, sLineComment));
            }

            if (item.Vi_bOddComments)
            {   /* Comments only Raw can edit: a marker. */
                if (iMutedFrom < 0)
                {   /* The grey part starts here. */
                    iMutedFrom = sLabel.Length;
                }
                sLabel += VTV_ODDMARK;
            }
        }

        tag.Vtt_iMutedFrom = iMutedFrom;
        tag.Vtt_iValueFrom = iValueFrom;
        tag.Vtt_iKeyFrom = iKeyFrom;
        tag.Vtt_iKeyLength = iKeyLength;
        tag.Vtt_iValueLength = iValueLength;
        tag.Vtt_iValueRole = iValueRole;
        if (treeNode.ForeColor != colorMain)
        {   /* A different colour: set it (Color.Empty is the tree's own). */
            treeNode.ForeColor = colorMain;
        }

        if (treeNode.Text != sLabel)
        {   /* Changed: set it (this redraws the node). */
            treeNode.Text = sLabel;
        }
    }

    /*------------------------------------------------------------------------*/
    /* Vtv_OneLine:                                                           */
    /*                                                                        */
    /* Text fit for a one-line label: each line break shown as a return       */
    /* arrow (CRLF as one), the indentation the next line starts with         */
    /* collapsed to a single space after the arrow (a multi-line comment's    */
    /* second line is usually indented to line up in the file, which only     */
    /* spreads the label out), tabs as spaces, cut to VTV_LEAFMAX characters  */
    /* with "..." if longer.                                                  */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     s : the text.                                                      */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the label text.                                           */
    /*------------------------------------------------------------------------*/
    public static string Vtv_OneLine(string s)
    {
        string sLine;                                    // the text on one line
        StringBuilder sb;                                // the text with indentation collapsed
        bool bAfterBreak;                                // the last character was a line break
        bool bSkipped;                                   // indentation was skipped after it
        int i;

        sLine = s.Replace("\r\n", VTV_RETURNMARK).Replace("\n", VTV_RETURNMARK)
            .Replace("\r", VTV_RETURNMARK);

        /* Indentation after each line break: one space. */
        sb = new StringBuilder(sLine.Length);
        bAfterBreak = false;
        bSkipped = false;
        for (i = 0; i < sLine.Length; i++)
        {
            if (bAfterBreak && (sLine[i] == ' ' || sLine[i] == '\t'))
            {   /* Indentation of the next line: skipped. */
                bSkipped = true;
                continue;
            }

            if (bSkipped)
            {   /* The indentation ended: one space in its place. */
                sb.Append(' ');
            }
            bSkipped = false;
            bAfterBreak = (sLine[i] == VTV_RETURNMARK[0]);
            sb.Append(sLine[i]);
        }

        if (bSkipped)
        {   /* Indentation at the very end: one space for it too. */
            sb.Append(' ');
        }
        sLine = sb.ToString().Replace('\t', ' ');

        if (sLine.Length > VTV_LEAFMAX)
        {   /* Too long for a label: cut it and show that it was cut. */
            sLine = sLine.Substring(0, VTV_LEAFMAX - 3) + "...";
        }

        /* Fit for a label. */
        return(sLine);
    }

    /*------------------------------------------------------------------------*/
    /* Vtv_PathOf:                                                            */
    /*                                                                        */
    /* A tree node's position path: its index at each level, top first,       */
    /* which is the item's path in the document.                              */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     treeNode : the node.                                               */
    /*                                                                        */
    /* Returns:                                                               */
    /*     List<int> : the path.                                              */
    /*------------------------------------------------------------------------*/
    public static List<int> Vtv_PathOf(TreeNode treeNode)
    {
        List<int> lstPath;                               // positions, gathered bottom first

        lstPath = new List<int>();
        while (treeNode != null)
        {
            lstPath.Add(treeNode.Index);
            treeNode = treeNode.Parent;
        }
        lstPath.Reverse();

        /* Top first. */
        return(lstPath);
    }

    /*------------------------------------------------------------------------*/
    /* Vtv_NodeAt:                                                            */
    /*                                                                        */
    /* The tree node at a path, optionally creating unpopulated levels on the */
    /* way down.  Without that, a path through a level not yet populated      */
    /* finds nothing.                                                         */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     lstPath      : the path.                                           */
    /*     bMaterialize : populate levels on the way.                         */
    /*     bNearest     : return the deepest node found when the path goes    */
    /*                    further than the tree does (or than the levels      */
    /*                    already populated, without bMaterialize).           */
    /*                                                                        */
    /* Returns:                                                               */
    /*     TreeNode : the node, the deepest one found (bNearest), or null.    */
    /*------------------------------------------------------------------------*/
    public TreeNode Vtv_NodeAt(List<int> lstPath, bool bMaterialize,
        bool bNearest)
    {
        TreeNodeCollection coll;                         // the level being looked in
        TreeNode treeNode;                               // the node reached
        int i;

        coll = Nodes;
        treeNode = null;
        for (i = 0; i < lstPath.Count; i++)
        {
            if (lstPath[i] < 0 || lstPath[i] >= coll.Count
                || coll[lstPath[i]].Tag == null)
            {   /* No node there. */
                if (bNearest)
                {   /* The deepest found. */
                    return(treeNode);
                }
                return(null);
            }

            treeNode = coll[lstPath[i]];
            if (i + 1 < lstPath.Count)
            {   /* Going deeper: the node's children must exist. */
                if (!((VoorheesTreeTag)treeNode.Tag).Vtt_bPopulated)
                {   /* Not populated. */
                    if (!bMaterialize)
                    {   /* Not asked to create it: stop. */
                        if (bNearest)
                        {   /* This is as deep as it goes. */
                            return(treeNode);
                        }
                        return(null);
                    }
                    treeNode = Vtv_Populate(treeNode);
                }
                coll = treeNode.Nodes;
            }
        }

        /* The node at the end of the path. */
        return(treeNode);
    }

    /*------------------------------------------------------------------------*/
    /* Vtv_Select:                                                            */
    /*                                                                        */
    /* Selects the item at a path (or its nearest existing ancestor), making  */
    /* the levels down to it, and scrolls it into view.  Selection events do  */
    /* fire: the editor loads its panel from them.                            */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     lstPath : the path.                                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : a node is selected, if the tree has any.                    */
    /*------------------------------------------------------------------------*/
    public void Vtv_Select(List<int> lstPath)
    {
        TreeNode treeNode;                               // the node to select

        treeNode = Vtv_NodeAt(lstPath, true, true);
        if (treeNode == null && Nodes.Count > 0)
        {   /* Nothing on the path: the first top-level node. */
            treeNode = Nodes[0];
        }

        if (treeNode != null)
        {   /* Select it and scroll it into view. */
            SelectedNode = treeNode;
            treeNode.EnsureVisible();
        }
    }

    /*------------------------------------------------------------------------*/
    /* Vtv_ExpandPath:                                                        */
    /*                                                                        */
    /* Opens the container at a path, populating it first.                    */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     lstPath : the container's path.                                    */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the node is populated and expanded, if it exists.           */
    /*------------------------------------------------------------------------*/
    public void Vtv_ExpandPath(List<int> lstPath)
    {
        TreeNode treeNode;                               // the container's node

        treeNode = Vtv_NodeAt(lstPath, true, false);
        if (treeNode == null)
        {   /* Not there. */
            return;
        }

        if (!((VoorheesTreeTag)treeNode.Tag).Vtt_bPopulated)
        {   /* Create its children first. */
            treeNode = Vtv_Populate(treeNode);
        }
        treeNode.Expand();
    }

    /*------------------------------------------------------------------------*/
    /* OnBeforeExpand:                                                        */
    /*                                                                        */
    /* The user is opening a node.  A populated one opens as usual.  An       */
    /* unpopulated one is held (the event is cancelled) and populated and     */
    /* opened once this event is over, through the message queue: the         */
    /* populate removes and re-inserts the node, which must not happen        */
    /* inside the control's own expand.                                       */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     e : the node; e.Cancel holds the expand.                           */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the node opens now, or is queued to.                        */
    /*------------------------------------------------------------------------*/
    protected override void OnBeforeExpand(TreeViewCancelEventArgs e)
    {
        List<int> lstPath;                               // the node's path

        if (e.Node.Tag != null && !((VoorheesTreeTag)e.Node.Tag).Vtt_bPopulated)
        {   /* Not populated: populate and open after this event. */
            e.Cancel = true;
            lstPath = Vtv_PathOf(e.Node);
            BeginInvoke((MethodInvoker)delegate { Vtv_ExpandPath(lstPath); });
            return;
        }

        base.OnBeforeExpand(e);
    }

    /*------------------------------------------------------------------------*/
    /* Vtv_Populate:                                                          */
    /*                                                                        */
    /* Creates a container node's children.  Adding children one at a time    */
    /* to a node already in the control is slow (the control walks to the end */
    /* of the sibling list for each: 40,000 children took about 20 s), but    */
    /* the control takes a whole DETACHED branch fast.  So: the node is taken */
    /* out of the tree (it holds only its placeholder), filled while          */
    /* detached, and put back at the same place, with the selection and the   */
    /* scroll position restored.  Big fills are counted for progress, the     */
    /* window put up first when VTV_FILLUSPERNODE says they will be slow.     */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     treeNode : the unpopulated container node.                         */
    /*                                                                        */
    /* Returns:                                                               */
    /*     TreeNode : the same node, now populated and back in the tree.      */
    /*------------------------------------------------------------------------*/
    TreeNode Vtv_Populate(TreeNode treeNode)
    {
        List<int> lstPath;                               // the container's path
        VoorheesNode container;                          // the container
        Dictionary<string, int> dictKeys;                // its key counts
        TreeNode[] arrChildren;                          // the new children
        TreeNodeCollection coll;                         // the collection the node is in
        TreeNode topNode;                                // the first visible node, to restore the scroll
        BusyModalForm prog;                              // progress window, shown only if this is slow
        bool bSelected;                                  // the node was selected
        int iIndex;                                      // its position in coll
        int iElement;                                    // array index of the next value
        Stopwatch swTime;                                // times a big populate for the debug log
        long lLabelMs;                                   // debug log: time to label the children
        int i;

        swTime = Stopwatch.StartNew();
        lstPath = Vtv_PathOf(treeNode);
        container = Vtv_doc.Vd_NodeAt(lstPath);
        dictKeys = Vtv_doc.Vd_KeyCounts(container);
        arrChildren = new TreeNode[container.Vn_Count()];

        prog = null;
        if (arrChildren.Length * VTV_FILLUSPERNODE / 1000 >= VTV_SHOWDELAYMS)
        {   /* Slow: progress, with the window up before the control's long call. */
            prog = BusyModalForm.BMF_BeginUiProgress(FindForm(), "Opening",
                Vtv_DocName(), "Voorhees", VTV_SHOWDELAYMS);
            prog.BMF_ShowNow();
        }

        Vtv_iBusyDepth++;
        try
        {
            /* Label every child: the first half of the progress.  Array   */
            /* indexes are counted on the way, one per value.              */
            iElement = 0;
            for (i = 0; i < arrChildren.Length; i++)
            {
                arrChildren[i] = Vtv_MakeNode(container, lstPath, i, dictKeys,
                    iElement);
                if (container.Vn_lstItems[i].Vi_IsValue())
                {   /* A value: the next one has the next index. */
                    iElement++;
                }

                if (prog != null && i % VTV_PROGRESSSTEP == 0)
                {   /* Report now and then. */
                    prog.BMF_ReportUiProgress(i, (long)arrChildren.Length * 2);
                }
            }
            lLabelMs = swTime.ElapsedMilliseconds;

            /* Take the node out, fill it, put it back. */
            BeginUpdate();
            topNode = TopNode;
            bSelected = (SelectedNode == treeNode);
            if (treeNode.Parent != null)
            {   /* A child: its parent's collection. */
                coll = treeNode.Parent.Nodes;
            }
            else
            {   /* Top level. */
                coll = Nodes;
            }
            iIndex = treeNode.Index;
            coll.RemoveAt(iIndex);
            treeNode.Nodes.Clear();
            treeNode.Nodes.AddRange(arrChildren);
            ((VoorheesTreeTag)treeNode.Tag).Vtt_bPopulated = true;

            /* The control inserts the branch: the second half, counted. */
            if (prog != null)
            {   /* Count the inserts. */
                Vtv_prog = prog;
                Vtv_lDone = arrChildren.Length;
                Vtv_lTotal = (long)arrChildren.Length * 2;
                Vtv_bCountInserts = true;
            }
            try
            {
                coll.Insert(iIndex, treeNode);
            }
            finally
            {
                Vtv_bCountInserts = false;
                Vtv_prog = null;
            }

            if (bSelected)
            {   /* It was selected: select it again. */
                SelectedNode = treeNode;
            }

            if (topNode != null && topNode.TreeView == this)
            {   /* Keep the view where it was. */
                TopNode = topNode;
            }
            EndUpdate();

            if (arrChildren.Length >= VTV_PROGRESSSTEP)
            {   /* A big one: worth a line in the debug log. */
                Program.APP_DebugLog("populated "
                    + arrChildren.Length.ToString(CultureInfo.InvariantCulture)
                    + " children: labelled in "
                    + lLabelMs.ToString(CultureInfo.InvariantCulture)
                    + " ms, in the tree after "
                    + swTime.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture)
                    + " ms");
            }
        }
        finally
        {
            Vtv_iBusyDepth--;
            if (prog != null)
            {   /* Close the progress window. */
                prog.BMF_EndUiProgress();
            }
        }

        /* The node, populated, in its place. */
        return(treeNode);
    }

    /*------------------------------------------------------------------------*/
    /* Vtv_ApplyChange:                                                       */
    /*                                                                        */
    /* Brings the tree up to date with one change the document has ALREADY    */
    /* made (or, with bReverse, already undone), touching only populated      */
    /* parts:                                                                 */
    /*     o the container's node not in the tree (an ancestor unpopulated):  */
    /*       nothing to do; it will be populated fresh;                       */
    /*     o the container's node unpopulated: only its placeholder follows   */
    /*       whether the container has items;                                 */
    /*     o SETITEM: the node is relabelled; if a container value was        */
    /*       replaced, the node is replaced (unpopulated) and opened again if */
    /*       it was open;                                                     */
    /*     o INSERT / REMOVE: the one node is inserted or removed;            */
    /* then the siblings whose labels depend on it are relabelled (array      */
    /* indexes after the position; duplicate marks of the keys involved).     */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     ch       : the change.                                             */
    /*     bReverse : it was undone rather than made.                         */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the tree matches the document.                              */
    /*------------------------------------------------------------------------*/
    public void Vtv_ApplyChange(VoorheesChange ch, bool bReverse)
    {
        TreeNode parentNode;                             // the container's node, or null for the top level
        TreeNodeCollection coll;                         // the container's child nodes
        VoorheesNode container;                          // the container, as it is now
        VoorheesItem itemBefore;                         // the item before the change
        VoorheesItem itemAfter;                          // the item after it
        bool bInsert;                                    // the change inserted an item
        bool bRemove;                                    // the change removed an item
        List<string> lstKeys;                            // keys whose duplicate marks may change

        /* What the change did, in the direction it was applied. */
        bInsert = false;
        bRemove = false;
        itemBefore = null;
        itemAfter = null;
        if (ch.Vch_iKind == VoorheesChange.VCH_SETITEM)
        {   /* One item replaced by another. */
            if (bReverse)
            {   /* Undone: new back to old. */
                itemBefore = ch.Vch_itemNew;
                itemAfter = ch.Vch_itemOld;
            }
            else
            {   /* Made: old to new. */
                itemBefore = ch.Vch_itemOld;
                itemAfter = ch.Vch_itemNew;
            }
        }
        else if ((ch.Vch_iKind == VoorheesChange.VCH_INSERT) != bReverse)
        {   /* An insert made, or a remove undone: an item went in. */
            bInsert = true;
            itemAfter = Vtv_doc.Vd_NodeAt(ch.Vch_lstPath).Vn_lstItems[ch.Vch_iPos];
        }
        else
        {   /* A remove made, or an insert undone: an item came out. */
            bRemove = true;
            if (bReverse)
            {   /* An undone insert: the item that had been inserted. */
                itemBefore = ch.Vch_itemNew;
            }
            else
            {   /* A remove: the item removed. */
                itemBefore = ch.Vch_itemOld;
            }
        }

        container = Vtv_doc.Vd_NodeAt(ch.Vch_lstPath);

        /* A step's changes are replayed against the document as the WHOLE     */
        /* step left it.  A replacement whose item is not (or no longer) at    */
        /* its position there is changed again by a later change of the        */
        /* same step, in the order replayed -- an entry added and named in     */
        /* one step (an include's file, a registry key's hive and path),       */
        /* undone: the name comes off first, then the entry goes.  That        */
        /* later change brings the node in line; labelling it now would read   */
        /* an item that is not there.                                          */
        if (ch.Vch_iKind == VoorheesChange.VCH_SETITEM
            && (ch.Vch_iPos >= container.Vn_Count()
            || !ReferenceEquals(container.Vn_lstItems[ch.Vch_iPos], itemAfter)))
        {   /* Superseded within the step: nothing to do now. */
            return;
        }

        /* Find the container's node; not in the tree means nothing to do. */
        if (ch.Vch_lstPath.Count == 0)
        {   /* The document: the top level. */
            parentNode = null;
            coll = Nodes;
        }
        else
        {   /* A container: its node, only if the tree has it. */
            parentNode = Vtv_NodeAt(ch.Vch_lstPath, false, false);
            if (parentNode == null)
            {   /* Inside a part never populated: it will be built fresh. */
                return;
            }

            if (!((VoorheesTreeTag)parentNode.Tag).Vtt_bPopulated)
            {   /* Unpopulated: only the placeholder needs to follow. */
                Vtv_SyncPlaceholder(parentNode, container);
                return;
            }
            coll = parentNode.Nodes;
        }

        Vtv_iBusyDepth++;
        try
        {
            BeginUpdate();
            if (bInsert)
            {   /* A new node at the position. */
                coll.Insert(ch.Vch_iPos, Vtv_MakeNode(container, ch.Vch_lstPath,
                    ch.Vch_iPos, Vtv_doc.Vd_KeyCounts(container), -1));
            }
            else if (bRemove)
            {   /* The node goes (counted if its branch is big). */
                EndUpdate();
                Vtv_RemoveNodesCounted(coll, ch.Vch_iPos);
                BeginUpdate();
            }
            else
            {   /* Replaced in place. */
                Vtv_ReplaceItemNode(coll, container, ch.Vch_lstPath, ch.Vch_iPos,
                    itemBefore, itemAfter);
            }

            /* Siblings whose labels depend on this one. */
            lstKeys = new List<string>();
            if (itemBefore != null && itemBefore.Vi_iKind == VoorheesItem.VI_MEMBER)
            {   /* Its old key's other copies. */
                lstKeys.Add(itemBefore.Vi_sKey);
            }

            if (itemAfter != null && itemAfter.Vi_iKind == VoorheesItem.VI_MEMBER)
            {   /* Its new key's other copies. */
                lstKeys.Add(itemAfter.Vi_sKey);
            }

            if (bInsert || bRemove || (itemBefore != null && itemAfter != null
                && (itemBefore.Vi_IsValue() != itemAfter.Vi_IsValue()
                || itemBefore.Vi_sKey != itemAfter.Vi_sKey)))
            {   /* Indexes or keys may have moved: relabel the siblings concerned. */
                Vtv_RelabelSiblings(coll, container, ch.Vch_lstPath, ch.Vch_iPos,
                    lstKeys, bInsert || bRemove);
            }

            if (parentNode != null)
            {   /* An empty container shows no expand box. */
                Vtv_SyncPlaceholder(parentNode, container);
            }
            EndUpdate();
        }
        finally
        {
            Vtv_iBusyDepth--;
        }
    }

    /*------------------------------------------------------------------------*/
    /* Vtv_ReplaceItemNode:                                                   */
    /*                                                                        */
    /* SETITEM's work on the tree.  When the value node is the same object    */
    /* (a rename, a comment edit) or neither value is a container, the node   */
    /* is only relabelled, keeping its children and open state.  Otherwise    */
    /* (a container replaced, or a value turned into or out of a container)   */
    /* the node is replaced by a new unpopulated one, opened again if the old */
    /* one was open, and selected again if it (or something inside it) was    */
    /* selected.                                                              */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     coll         : the container's child nodes.                        */
    /*     container    : the container.                                      */
    /*     lstContainer : its path.                                           */
    /*     iPos         : the item's position.                                */
    /*     itemBefore   : the item before.                                    */
    /*     itemAfter    : the item after.                                     */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the node shows the new item.                                */
    /*------------------------------------------------------------------------*/
    void Vtv_ReplaceItemNode(TreeNodeCollection coll, VoorheesNode container,
        List<int> lstContainer, int iPos, VoorheesItem itemBefore,
        VoorheesItem itemAfter)
    {
        TreeNode oldNode;                                // the node now
        TreeNode newNode;                                // its replacement
        TreeNode selected;                               // the selected node
        bool bExpanded;                                  // the old node was open
        bool bWasSelected;                               // it or something in it was selected
        bool bSameValue;                                 // the value node did not change
        bool bContainers;                                // a container is involved

        oldNode = coll[iPos];
        bSameValue = itemBefore.Vi_IsValue() && itemAfter.Vi_IsValue()
            && ReferenceEquals(itemBefore.Vi_node, itemAfter.Vi_node);
        bContainers = (itemBefore.Vi_IsValue() && itemBefore.Vi_node.Vn_IsContainer())
            || (itemAfter.Vi_IsValue() && itemAfter.Vi_node.Vn_IsContainer());

        if (bSameValue || !bContainers)
        {   /* Only the label changes. */
            Vtv_SetLabel(oldNode, container, lstContainer, iPos,
                Vtv_doc.Vd_KeyCounts(container), -1);
            return;
        }

        /* A container came or went: a fresh node in its place. */
        bExpanded = oldNode.IsExpanded;
        selected = SelectedNode;
        bWasSelected = false;
        while (selected != null)
        {
            if (selected == oldNode)
            {   /* The selection is this node or inside it. */
                bWasSelected = true;
                break;
            }
            selected = selected.Parent;
        }

        newNode = Vtv_MakeNode(container, lstContainer, iPos,
            Vtv_doc.Vd_KeyCounts(container), -1);
        Vtv_RemoveNodesCounted(coll, iPos);
        coll.Insert(iPos, newNode);

        if (bExpanded && newNode.Nodes.Count > 0)
        {   /* It was open: open the new one. */
            newNode = Vtv_Populate(newNode);
            newNode.Expand();
        }

        if (bWasSelected)
        {   /* Keep the selection on it. */
            SelectedNode = newNode;
        }
    }

    /*------------------------------------------------------------------------*/
    /* Vtv_RelabelSiblings:                                                   */
    /*                                                                        */
    /* Relabels the nodes of a container whose labels may have changed: with  */
    /* bIndexes, every unkeyed value (array element) from a position on       */
    /* (their indexes moved) -- and every member whose key is in a list,      */
    /* compared as the format compares keys (duplicate marks).  Only labels   */
    /* that differ are set.                                                   */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     coll         : the container's child nodes.                        */
    /*     container    : the container.                                      */
    /*     lstContainer : its path.                                           */
    /*     iFrom        : first position whose index may have moved.          */
    /*     lstKeys      : keys whose members to relabel.                      */
    /*     bIndexes     : indexes may have moved.                             */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the labels are current.                                     */
    /*------------------------------------------------------------------------*/
    void Vtv_RelabelSiblings(TreeNodeCollection coll, VoorheesNode container,
        List<int> lstContainer, int iFrom, List<string> lstKeys, bool bIndexes)
    {
        Dictionary<string, int> dictKeys;                // the container's key counts
        StringComparer comparer;                         // how its keys compare
        VoorheesItem item;                               // an item
        int iElement;                                    // array index of the value at i
        int i;

        dictKeys = Vtv_doc.Vd_KeyCounts(container);
        comparer = Vtv_doc.Vd_KeyComparer(container);
        iElement = 0;
        for (i = 0; i < coll.Count && i < container.Vn_Count(); i++)
        {
            item = container.Vn_lstItems[i];
            if (bIndexes && i >= iFrom
                && item.Vi_iKind == VoorheesItem.VI_ELEMENT)
            {   /* An unkeyed value (a JSON array element) whose index may have moved. */
                Vtv_SetLabel(coll[i], container, lstContainer, i, dictKeys,
                    iElement);
            }
            else if (item.Vi_iKind == VoorheesItem.VI_MEMBER
                && Vtv_KeyListed(lstKeys, item.Vi_sKey, comparer))
            {   /* A member sharing a key involved: its duplicate mark may change. */
                Vtv_SetLabel(coll[i], container, lstContainer, i, dictKeys, -1);
            }

            if (item.Vi_IsValue())
            {   /* A value: the next one has the next index. */
                iElement++;
            }
        }
    }

    /*------------------------------------------------------------------------*/
    /* Vtv_KeyListed:                                                         */
    /*                                                                        */
    /* Whether a key is in a list of keys, compared as the container's format */
    /* compares keys (so a format that ignores case finds "Name" for "name"). */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     lstKeys  : the keys.                                               */
    /*     sKey     : the key to look for.                                    */
    /*     comparer : how keys compare (Vd_KeyComparer).                      */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true when one of them is the same key.                      */
    /*------------------------------------------------------------------------*/
    static bool Vtv_KeyListed(List<string> lstKeys, string sKey,
        StringComparer comparer)
    {
        int i;

        for (i = 0; i < lstKeys.Count; i++)
        {
            if (comparer.Equals(lstKeys[i], sKey))
            {   /* The same key. */
                return(true);
            }
        }

        /* Not listed. */
        return(false);
    }

    /*------------------------------------------------------------------------*/
    /* Vtv_SyncPlaceholder:                                                   */
    /*                                                                        */
    /* Keeps an unpopulated container's placeholder in step with whether the  */
    /* container has items (an expand box only when there is something to     */
    /* open), and marks a populated empty container as such.                  */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     treeNode  : the container's node.                                  */
    /*     container : the container.                                         */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the placeholder is added or removed as needed.              */
    /*------------------------------------------------------------------------*/
    static void Vtv_SyncPlaceholder(TreeNode treeNode, VoorheesNode container)
    {
        VoorheesTreeTag tag;                             // the node's tag

        tag = (VoorheesTreeTag)treeNode.Tag;
        if (tag.Vtt_bPopulated)
        {   /* Populated: its children are real; nothing to sync. */
            return;
        }

        if (container.Vn_Count() > 0 && treeNode.Nodes.Count == 0)
        {   /* Has items now: give it its expand box. */
            treeNode.Nodes.Add(new TreeNode());
        }
        else if (container.Vn_Count() == 0 && treeNode.Nodes.Count > 0)
        {   /* Empty now: nothing to open, and nothing left to populate. */
            treeNode.Nodes.Clear();
            tag.Vtt_bPopulated = true;
        }
    }

    /*------------------------------------------------------------------------*/
    /* Vtv_SetShowRaw:                                                        */
    /*                                                                        */
    /* Switches the raw-text labels on or off and relabels every node that    */
    /* exists.                                                                */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     bShowRaw : raw labels wanted.                                      */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : every label is redone.                                      */
    /*------------------------------------------------------------------------*/
    public void Vtv_SetShowRaw(bool bShowRaw)
    {
        Vtv_bShowRaw = bShowRaw;
        if (Vtv_doc == null)
        {   /* No document yet. */
            return;
        }

        BeginUpdate();
        Vtv_RelabelAll(Nodes, Vtv_doc.Vd_root, new List<int>());
        EndUpdate();
    }

    /*------------------------------------------------------------------------*/
    /* Vtv_SetColorKeys:                                                      */
    /*                                                                        */
    /* Switches coloured keys on or off (View > Color Keys).  Labels do not   */
    /* change -- only how they are drawn -- so the tree is just repainted.    */
    /* The colours are the role tables' (Vtv_arrNameColors,                   */
    /* Vtv_arrValueColors); a change to those is shown the same way, by a     */
    /* repaint.                                                               */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     bColorKeys : names and values wanted in their roles' colours.      */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the tree repaints.                                          */
    /*------------------------------------------------------------------------*/
    public void Vtv_SetColorKeys(bool bColorKeys)
    {
        Vtv_bColorKeys = bColorKeys;
        Invalidate();
    }

    /*------------------------------------------------------------------------*/
    /* Vtv_RelabelAll:                                                        */
    /*                                                                        */
    /* Relabels a level and every populated level below it.                   */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     coll         : the level's nodes.                                  */
    /*     container    : the container they show.                            */
    /*     lstContainer : its path.                                           */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the labels are current.                                     */
    /*------------------------------------------------------------------------*/
    void Vtv_RelabelAll(TreeNodeCollection coll, VoorheesNode container,
        List<int> lstContainer)
    {
        Dictionary<string, int> dictKeys;                // the container's key counts
        List<int> lstChild;                              // a child's path
        int iElement;                                    // array index of the value at i
        int i;

        dictKeys = Vtv_doc.Vd_KeyCounts(container);
        iElement = 0;
        for (i = 0; i < coll.Count && i < container.Vn_Count(); i++)
        {
            Vtv_SetLabel(coll[i], container, lstContainer, i, dictKeys, iElement);
            if (container.Vn_lstItems[i].Vi_IsValue())
            {   /* A value: the next one has the next index. */
                iElement++;
            }

            if (((VoorheesTreeTag)coll[i].Tag).Vtt_bPopulated && coll[i].Nodes.Count > 0)
            {   /* Its children exist: relabel them too. */
                lstChild = new List<int>(lstContainer);
                lstChild.Add(i);
                Vtv_RelabelAll(coll[i].Nodes, container.Vn_lstItems[i].Vi_node,
                    lstChild);
            }
        }
    }

    /*------------------------------------------------------------------------*/
    /* Vtv_RefreshLabel:                                                      */
    /*                                                                        */
    /* Relabels one node from the document (after an in-place edit box put    */
    /* other text on it, say).                                                */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     treeNode : the node.                                               */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : its label is current.                                       */
    /*------------------------------------------------------------------------*/
    public void Vtv_RefreshLabel(TreeNode treeNode)
    {
        List<int> lstPath;                               // the node's path
        List<int> lstContainer;                          // its container's path
        VoorheesNode container;                          // its container

        if (treeNode == null || treeNode.Tag == null || treeNode.TreeView != this)
        {   /* Not a real node of this tree. */
            return;
        }

        lstPath = Vtv_PathOf(treeNode);
        lstContainer = lstPath.GetRange(0, lstPath.Count - 1);
        container = Vtv_doc.Vd_NodeAt(lstContainer);
        if (container == null || lstPath[lstPath.Count - 1] >= container.Vn_Count())
        {   /* Out of step: nothing to show. */
            return;
        }

        Vtv_SetLabel(treeNode, container, lstContainer, lstPath[lstPath.Count - 1],
            Vtv_doc.Vd_KeyCounts(container), -1);
    }

    /*------------------------------------------------------------------------*/
    /* Vtv_PrepareValueEdit:                                                  */
    /*                                                                        */
    /* Gets a node ready for an in-place edit of its value (call it just      */
    /* before TreeNode.BeginEdit).  The control's own edit box covers the     */
    /* whole label and starts with the label's text; for a value that would   */
    /* hide the key while typing, and the label's text is cut to              */
    /* VTV_LEAFMAX and so cannot be what is edited.  So:                      */
    /*     o the label is cut back to its key part ("key: ", "3: ",           */
    /*       "comment: "), with no grey tail or value run, so the key stays   */
    /*       readable;                                                        */
    /*     o the full value is kept for the edit box, which OnBeforeLabelEdit */
    /*       fills with it;                                                   */
    /*     o the edit box is moved right by the key part's measured width     */
    /*       (VoorheesTreeEditShifter), so it starts where the value did.     */
    /* A label that is all value (the raw-text view) is replaced by the value */
    /* and the box is not moved.  Vtv_EndValueEdit and Vtv_RefreshLabel put   */
    /* everything back.                                                       */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     treeNode : the node.                                               */
    /*     sValue   : the full text to edit.                                  */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the node shows its key part; the edit is prepared.          */
    /*------------------------------------------------------------------------*/
    public void Vtv_PrepareValueEdit(TreeNode treeNode, string sValue)
    {
        VoorheesTreeTag tag;                             // the node's tag
        string sPrefix;                                  // the label's key part

        tag = (VoorheesTreeTag)treeNode.Tag;
        sPrefix = "";
        if (tag.Vtt_iValueFrom > 0 && tag.Vtt_iValueFrom <= treeNode.Text.Length)
        {   /* The label has a key part: keep it in view. */
            sPrefix = treeNode.Text.Substring(0, tag.Vtt_iValueFrom);
        }

        Vtv_EndValueEdit();
        Vtv_sEditValue = sValue;
        Vtv_iEditShift = 0;
        tag.Vtt_iMutedFrom = -1;

        /* The value leaves the label for the box: no value run to colour. */
        tag.Vtt_iValueLength = 0;
        tag.Vtt_iValueRole = -1;
        if (sPrefix.Length > 0)
        {   /* The key part stays; the box goes after it. */
            Vtv_iEditShift = Vtv_TextWidth(sPrefix);
            treeNode.Text = sPrefix;
        }
        else
        {   /* All value: the box takes the label's place, as normal. */
            treeNode.Text = sValue;
        }
    }

    /*------------------------------------------------------------------------*/
    /* Vtv_EndValueEdit:                                                      */
    /*                                                                        */
    /* Forgets a prepared or finished in-place edit: the value and the box's  */
    /* shift are dropped and the box is no longer watched.  The node's label  */
    /* is put back separately (Vtv_RefreshLabel).                             */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : no in-place edit is prepared.                               */
    /*------------------------------------------------------------------------*/
    public void Vtv_EndValueEdit()
    {
        Vtv_sEditValue = null;
        Vtv_iEditShift = 0;
        Vtv_editNode = null;
        if (Vtv_editShifter != null)
        {   /* Stop watching the edit box. */
            Vtv_editShifter.ReleaseHandle();
            Vtv_editShifter = null;
        }
    }

    /*------------------------------------------------------------------------*/
    /* OnBeforeLabelEdit:                                                     */
    /*                                                                        */
    /* An in-place edit is starting.  The event goes to the editor first      */
    /* (which refuses edits it did not start).  If it goes ahead and was      */
    /* prepared by Vtv_PrepareValueEdit, the control's edit box -- which      */
    /* exists by now but is not yet placed or shown -- gets the full value,   */
    /* all of it selected, and is watched so that wherever the control puts   */
    /* it (now, and again as it widens with typing) it sits after the key     */
    /* part of the label.                                                     */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     e : the node; e.CancelEdit stops the edit.                         */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the edit box is filled and watched, or the edit cancelled.  */
    /*------------------------------------------------------------------------*/
    protected override void OnBeforeLabelEdit(NodeLabelEditEventArgs e)
    {
        IntPtr hEdit;                                    // the control's edit box

        base.OnBeforeLabelEdit(e);
        if (e.CancelEdit || Vtv_sEditValue == null)
        {   /* Refused, or not one of ours: nothing to set up. */
            return;
        }

        hEdit = Vtv_SendMessage(Handle, VTV_TVM_GETEDITCONTROL, IntPtr.Zero, IntPtr.Zero);
        if (hEdit == IntPtr.Zero)
        {   /* No box (should not happen): the edit goes on as the control does it. */
            return;
        }

        /* The full value, all selected, as the control would select its text. */
        Vtv_SendMessageText(hEdit, VTV_WM_SETTEXT, IntPtr.Zero, Vtv_sEditValue);
        Vtv_SendMessage(hEdit, VTV_EM_SETSEL, IntPtr.Zero, new IntPtr(-1));

        if (Vtv_iEditShift > 0)
        {   /* A key part to keep in view: move the box after it, every time it is placed. */
            Vtv_editShifter = VoorheesTreeEditShifter.Vtes_Create(hEdit, Vtv_iEditShift);

            /* The control leaves an edited item's label blank: the key   */
            /* part is drawn at its post-paint (Vtv_DrawEditKey).         */
            Vtv_editNode = e.Node;
            Invalidate(e.Node.Bounds);
        }
    }

    /*------------------------------------------------------------------------*/
    /* OnAfterLabelEdit:                                                      */
    /*                                                                        */
    /* An in-place edit has ended: the editor handles the event (it commits   */
    /* the text and relabels the node), then the edit's preparation is        */
    /* forgotten.                                                             */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     e : the node and the text typed (null if cancelled).               */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the edit is over.                                           */
    /*------------------------------------------------------------------------*/
    protected override void OnAfterLabelEdit(NodeLabelEditEventArgs e)
    {
        base.OnAfterLabelEdit(e);
        Vtv_EndValueEdit();
    }

    /*------------------------------------------------------------------------*/
    /* Vtv_SendMessage / Vtv_SendMessageText:                                 */
    /*                                                                        */
    /* user32.dll SendMessageW, imported twice under two names (one with a    */
    /* pointer-sized lParam, one with a text lParam), since no name may be    */
    /* overloaded.  Sends a message and waits for it to be handled.           */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     hWnd   : the window.                                               */
    /*     iMsg   : the message.                                              */
    /*     wParam : its first parameter.                                      */
    /*     lParam : its second parameter (a pointer-sized value, or text).    */
    /*                                                                        */
    /* Returns:                                                               */
    /*     IntPtr : the message's result.                                     */
    /*------------------------------------------------------------------------*/
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    static extern IntPtr Vtv_SendMessage(IntPtr hWnd, int iMsg, IntPtr wParam,
        IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)]
    static extern IntPtr Vtv_SendMessageText(IntPtr hWnd, int iMsg, IntPtr wParam,
        string lParam);

    /*------------------------------------------------------------------------*/
    /* Vtv_PostPaintItem:                                                     */
    /*                                                                        */
    /* Called at an item's post-paint stage, after the control has drawn it,  */
    /* to finish what the control cannot draw itself:                         */
    /*     o the item being edited in place shows its key part next to the    */
    /*       edit box: the control leaves the label of an item under edit     */
    /*       blank (just the selection colour), so the key part is drawn      */
    /*       here in the node's own colour on the plain background            */
    /*       (Vtv_DrawEditKey);                                               */
    /*     o any other item has, over what the control drew in the node's     */
    /*       colour, its key repainted in its name role's colour and its      */
    /*       value in its type's colour (View > Color Keys on; not a repeated */
    /*       key, which stays red throughout) and its grey                    */
    /*       tail (line comment or marker) repainted grey.  Not for the       */
    /*       selected item: the control draws that in the selection colours,  */
    /*       which read the same for the whole label, as anywhere else in     */
    /*       Windows.                                                         */
    /* The item is found from the row the control painted (a hit test on      */
    /* that row, checked against the item handle it named).  The parts are    */
    /* drawn at Windows' own character positions (Vtv_DrawLabelRuns).         */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     hdc        : the device context the control is painting with.      */
    /*     hItem      : the item's handle (HTREEITEM).                        */
    /*     rcRow      : the row the item occupies.                            */
    /*     iItemState : the item's CDIS_ state bits.                          */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the key, value and tail are coloured, or the edited item's  */
    /*            key part drawn.                                             */
    /*------------------------------------------------------------------------*/
    void Vtv_PostPaintItem(IntPtr hdc, IntPtr hItem, Rectangle rcRow, int iItemState)
    {
        TreeNode treeNode;                               // the item's node
        VoorheesTreeTag tag;                             // its tag
        Color colorBack;                                 // the label's background
        Color colorMain;                                 // the label's own colour (what the control drew it in)
        Color colorValue;                                // the value run's colour, by its role
        bool bKey;                                       // the key is to be coloured
        bool bValue;                                     // the value is to be coloured
        bool bTail;                                      // there is a grey tail
        int iKeyFrom;                                    // the key run's start, or -1
        int iValueFrom;                                  // the value run's start, or -1
        int iTailFrom;                                   // the tail's start, or -1

        if (rcRow.Height <= 0
            || ((iItemState & VTV_CDIS_SELECTED) != 0 && Vtv_editNode == null))
        {   /* Nothing to draw, or the selected item with no edit open (selection colours throughout). */
            return;
        }

        /* The node: the item on the painted row, if it is the one painted. */
        treeNode = GetNodeAt(Math.Max(0, rcRow.Left) + 1, rcRow.Top + rcRow.Height / 2);
        if (treeNode == null || treeNode.Handle != hItem)
        {   /* Not found (should not happen): leave the label as drawn. */
            return;
        }

        colorBack = treeNode.BackColor;
        if (colorBack.IsEmpty)
        {   /* No background of its own: the tree's. */
            colorBack = BackColor;
        }

        if (treeNode == Vtv_editNode)
        {   /* Being edited in place: its key part beside the box. */
            Vtv_DrawEditKey(hdc, treeNode, colorBack);
            return;
        }

        if ((iItemState & VTV_CDIS_SELECTED) != 0)
        {   /* Selected: selection colours throughout. */
            return;
        }

        tag = treeNode.Tag as VoorheesTreeTag;
        if (tag == null)
        {   /* Not a real node. */
            return;
        }

        /* What needs repainting: the key in its colour (option on, and   */
        /* the label in the tree's own colour, so not a repeated key's    */
        /* red), and the grey tail.                                       */
        colorMain = Vtv_NodeTextColor(treeNode);
        bKey = Vtv_bColorKeys && treeNode.ForeColor.IsEmpty && tag.Vtt_iKeyFrom >= 0
            && tag.Vtt_iKeyLength > 0
            && tag.Vtt_iKeyFrom + tag.Vtt_iKeyLength <= treeNode.Text.Length;
        bTail = tag.Vtt_iMutedFrom > 0 && tag.Vtt_iMutedFrom < treeNode.Text.Length;

        /* The value in its type's colour: the same conditions as the      */
        /* key, a value shown in the label, and a colour that differs      */
        /* from what the control already drew (with the defaults, black    */
        /* on black, there is nothing to repaint).  Colours compare by     */
        /* their RGB values: SystemColors.WindowText and Color.Black are   */
        /* different Color objects for the same black.                     */
        colorValue = colorMain;
        bValue = false;
        if (Vtv_bColorKeys && treeNode.ForeColor.IsEmpty
            && tag.Vtt_iValueRole >= 0 && tag.Vtt_iValueRole < Vtv_arrValueColors.Length
            && tag.Vtt_iValueLength > 0
            && tag.Vtt_iValueFrom + tag.Vtt_iValueLength <= treeNode.Text.Length)
        {   /* A value shown, coloured keys on: its role's colour. */
            colorValue = Vtv_arrValueColors[tag.Vtt_iValueRole];
            bValue = colorValue.ToArgb() != colorMain.ToArgb();
        }

        if (!bKey && !bValue && !bTail)
        {   /* None: the control's drawing is complete. */
            return;
        }

        /* The label again, in runs of colour. */
        iKeyFrom = -1;
        if (bKey)
        {   /* The key, in the key colour. */
            iKeyFrom = tag.Vtt_iKeyFrom;
        }

        iValueFrom = -1;
        if (bValue)
        {   /* The value, in its role's colour. */
            iValueFrom = tag.Vtt_iValueFrom;
        }

        iTailFrom = -1;
        if (bTail)
        {   /* The tail, grey. */
            iTailFrom = tag.Vtt_iMutedFrom;
        }

        Vtv_DrawLabelRuns(hdc, treeNode, colorBack, colorMain, iKeyFrom,
            tag.Vtt_iKeyLength, iValueFrom, tag.Vtt_iValueLength, colorValue,
            iTailFrom);
    }

    /*------------------------------------------------------------------------*/
    /* Vtv_NodeTextColor:                                                     */
    /*                                                                        */
    /* A node's text colour: its own (red for a repeated key, grey for a      */
    /* comment entry), or the tree's.                                         */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     treeNode : the node.                                               */
    /*                                                                        */
    /* Returns:                                                               */
    /*     Color : the colour its label is drawn in.                          */
    /*------------------------------------------------------------------------*/
    Color Vtv_NodeTextColor(TreeNode treeNode)
    {
        if (treeNode.ForeColor.IsEmpty)
        {   /* No colour of its own: the tree's. */
            return(ForeColor);
        }

        /* Its own. */
        return(treeNode.ForeColor);
    }

    /*------------------------------------------------------------------------*/
    /* Vtv_DrawLabelRuns:                                                     */
    /*                                                                        */
    /* Draws a label afresh in runs of colour -- main colour, the key in its  */
    /* name role's colour (Vtv_NameColor), the value in its type's colour,    */
    /* the tail grey -- over what the control drew.  Every                    */
    /* character goes exactly where the control put it, because both the      */
    /* positions and the drawing are Windows' own:                            */
    /*     o the control's own font (Vtv_ControlFont) is selected into its    */
    /*       device context, and GDI's GetTextExtentExPoint gives the running */
    /*       width after every character -- the measurement the control lays  */
    /*       its text out by;                                                 */
    /*     o the text starts a margin into the label rectangle, which is half */
    /*       of what the rectangle has beyond the text's width (the control   */
    /*       pads both sides equally), and is centred vertically in it;       */
    /*     o the whole label rectangle is cleared to the background, then     */
    /*       each run is drawn with GDI's ExtTextOut (as the control draws)   */
    /*       at its first character's position, with a transparent            */
    /*       background so runs never clip each other.                        */
    /* (WinForms' TextRenderer measures some fonts and sizes a few pixels     */
    /* differently from GDI; patching only the coloured part by its           */
    /* measurement once landed off the control's characters and clipped the   */
    /* brackets and colon next to a key at 10 point.)  The device context's   */
    /* font, colours and background mode are put back afterwards.             */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     hdc        : the device context the control is painting with.      */
    /*     treeNode   : the node.                                             */
    /*     colorBack  : the background.                                       */
    /*     colorMain  : the colour of everything not in a run below.          */
    /*     iKeyFrom     : first character of the key run, or -1 for none.     */
    /*     iKeyLength   : the key run's length.                               */
    /*     iValueFrom   : first character of the value run, or -1 for none.   */
    /*     iValueLength : the value run's length.                             */
    /*     colorValue   : the value run's colour (its type's role colour).    */
    /*     iTailFrom    : first character of the grey tail, or -1 for none.   */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the label is drawn.                                         */
    /*------------------------------------------------------------------------*/
    void Vtv_DrawLabelRuns(IntPtr hdc, TreeNode treeNode, Color colorBack,
        Color colorMain, int iKeyFrom, int iKeyLength, int iValueFrom,
        int iValueLength, Color colorValue, int iTailFrom)
    {
        string sText;                                    // the label
        Rectangle rcLabel;                               // its rectangle
        VtvRect rcFill;                                  // the same, for ExtTextOut
        int[] arrRunning;                                // running width after each character
        VtvSize size;                                    // the whole text's size
        IntPtr hFont;                                    // the font, as a GDI font
        IntPtr hOldFont;                                 // the font it replaced
        int iOldTextColor;                               // the text colour it replaced
        int iOldBackColor;                               // the background colour it replaced
        int iOldBackMode;                                // the background mode it replaced
        int iMargin;                                     // the text's margin in the label
        int y;                                           // the text's top
        int iRunFrom;                                    // first character of the run being drawn
        Color colorRun;                                  // its colour
        Color colorChar;                                 // one character's colour
        Color colorKey;                                  // the name's colour, by its role
        int i;

        sText = treeNode.Text;
        rcLabel = treeNode.Bounds;
        if (sText.Length == 0 || rcLabel.Width <= 0)
        {   /* Nothing to draw. */
            return;
        }

        /* The font the control itself draws with (not one made from the   */
        /* Font object: that can come out a different size).               */
        hFont = Vtv_ControlFont();
        hOldFont = Vtv_SelectObject(hdc, hFont);
        try
        {
            /* Windows' own layout of the text. */
            arrRunning = new int[sText.Length];
            Vtv_GetTextExtentExPoint(hdc, sText, sText.Length, 0, IntPtr.Zero,
                arrRunning, out size);
            iMargin = Math.Max(0, (rcLabel.Width - size.Vsz_cx) / 2);
            y = rcLabel.Top + (rcLabel.Height - size.Vsz_cy) / 2;

            /* Clear the label to the background. */
            rcFill.Vrc_iLeft = rcLabel.Left;
            rcFill.Vrc_iTop = rcLabel.Top;
            rcFill.Vrc_iRight = rcLabel.Right;
            rcFill.Vrc_iBottom = rcLabel.Bottom;
            iOldBackColor = Vtv_SetBkColor(hdc, ColorTranslator.ToWin32(colorBack));
            Vtv_ExtTextOutFill(hdc, 0, 0, VTV_ETO_OPAQUE, ref rcFill, "", 0, IntPtr.Zero);

            /* Each run of one colour, at its first character's place. */
            iOldBackMode = Vtv_SetBkMode(hdc, VTV_TRANSPARENT);
            iOldTextColor = Vtv_SetTextColor(hdc, ColorTranslator.ToWin32(colorMain));
            iRunFrom = 0;
            colorKey = Vtv_NameColor(treeNode);
            colorRun = Vtv_RunColor(0, colorMain, colorKey, iKeyFrom, iKeyLength,
                colorValue, iValueFrom, iValueLength, iTailFrom);
            for (i = 1; i <= sText.Length; i++)
            {
                colorChar = colorRun;
                if (i < sText.Length)
                {   /* A character: its colour. */
                    colorChar = Vtv_RunColor(i, colorMain, colorKey, iKeyFrom,
                        iKeyLength, colorValue, iValueFrom, iValueLength, iTailFrom);
                }

                if (i == sText.Length || colorChar != colorRun)
                {   /* The run ends here: draw it. */
                    Vtv_SetTextColor(hdc, ColorTranslator.ToWin32(colorRun));
                    Vtv_ExtTextOut(hdc, rcLabel.Left + iMargin + Vtv_EdgeAt(arrRunning, iRunFrom),
                        y, 0, IntPtr.Zero, sText.Substring(iRunFrom, i - iRunFrom),
                        i - iRunFrom, IntPtr.Zero);
                    iRunFrom = i;
                    colorRun = colorChar;
                }
            }

            /* The device context as the control left it. */
            Vtv_SetTextColor(hdc, iOldTextColor);
            Vtv_SetBkMode(hdc, iOldBackMode);
            Vtv_SetBkColor(hdc, iOldBackColor);
        }
        finally
        {
            /* The control's font is the control's: put back, never freed. */
            Vtv_SelectObject(hdc, hOldFont);
        }
    }

    /*------------------------------------------------------------------------*/
    /* Vtv_ControlFont:                                                       */
    /*                                                                        */
    /* The GDI font the control draws its labels with, asked of the control   */
    /* itself (WM_GETFONT).  Measuring with exactly this font is what makes   */
    /* the character positions match the control's; a font made from the      */
    /* WinForms Font object (Font.ToHfont) measured 7 pixels narrower on      */
    /* "name: " at 8.25 point.  The handle belongs to the control and must    */
    /* not be freed.                                                          */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     IntPtr : the font handle.                                          */
    /*------------------------------------------------------------------------*/
    IntPtr Vtv_ControlFont()
    {
        /* The control's answer. */
        return(Vtv_SendMessage(Handle, VTV_WM_GETFONT, IntPtr.Zero, IntPtr.Zero));
    }

    /*------------------------------------------------------------------------*/
    /* Vtv_NameColor:                                                         */
    /*                                                                        */
    /* The colour of a node's name: a top-level                               */
    /* container's, a nested container's, or a plain entry's, from            */
    /* Vtv_arrNameColors.                                                     */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     treeNode : the node.                                               */
    /*                                                                        */
    /* Returns:                                                               */
    /*     Color : its name's colour.                                         */
    /*------------------------------------------------------------------------*/
    Color Vtv_NameColor(TreeNode treeNode)
    {
        VoorheesNode node;                               // the node's value

        node = null;
        if (Vtv_doc != null)
        {   /* A document: the node's value. */
            node = Vtv_doc.Vd_NodeAt(Vtv_PathOf(treeNode));
        }

        if (node != null && node.Vn_IsContainer() && treeNode.Parent == null)
        {   /* A top-level container. */
            return(Vtv_arrNameColors[VTV_NAME_TOP]);
        }

        if (node != null && node.Vn_IsContainer())
        {   /* A container inside another. */
            return(Vtv_arrNameColors[VTV_NAME_NESTED]);
        }

        /* A plain entry. */
        return(Vtv_arrNameColors[VTV_NAME_ENTRY]);
    }

    /*------------------------------------------------------------------------*/
    /* Vtv_RunColor:                                                          */
    /*                                                                        */
    /* The colour one character of a label is drawn in (Vtv_DrawLabelRuns):   */
    /* grey from the tail on, the name's colour inside the key (its role's:   */
    /* Vtv_NameColor), the value's colour inside the value (its type's role), */
    /* else the main colour.  The runs never overlap (the label is name,      */
    /* separator, value, then the tail), so the order of the tests only       */
    /* matters for a run that was cut short; the tail is tested first, as it  */
    /* always ends the label.                                                 */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     i            : the character.                                      */
    /*     colorMain    : the main colour.                                    */
    /*     colorKey     : the name's colour.                                  */
    /*     iKeyFrom     : first character of the key, or -1 for none.         */
    /*     iKeyLength   : the key's length.                                   */
    /*     colorValue   : the value's colour.                                 */
    /*     iValueFrom   : first character of the value, or -1 for none.       */
    /*     iValueLength : the value's length.                                 */
    /*     iTailFrom    : first character of the tail, or -1 for none.        */
    /*                                                                        */
    /* Returns:                                                               */
    /*     Color : its colour.                                                */
    /*------------------------------------------------------------------------*/
    static Color Vtv_RunColor(int i, Color colorMain, Color colorKey, int iKeyFrom,
        int iKeyLength, Color colorValue, int iValueFrom, int iValueLength,
        int iTailFrom)
    {
        if (iTailFrom >= 0 && i >= iTailFrom)
        {   /* In the tail. */
            return(VTV_COLORMUTED);
        }

        if (iKeyFrom >= 0 && i >= iKeyFrom && i < iKeyFrom + iKeyLength)
        {   /* In the key: its role's colour. */
            return(colorKey);
        }

        if (iValueFrom >= 0 && i >= iValueFrom && i < iValueFrom + iValueLength)
        {   /* In the value: its type's colour. */
            return(colorValue);
        }

        /* Anywhere else. */
        return(colorMain);
    }

    /*------------------------------------------------------------------------*/
    /* Vtv_EdgeAt:                                                            */
    /*                                                                        */
    /* Where a character starts, from GetTextExtentExPoint's running widths:  */
    /* character i starts where the text before it ends.                      */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     arrRunning : running width after each character.                   */
    /*     i          : the character.                                        */
    /*                                                                        */
    /* Returns:                                                               */
    /*     int : its left edge, pixels from the text's start.                 */
    /*------------------------------------------------------------------------*/
    static int Vtv_EdgeAt(int[] arrRunning, int i)
    {
        if (i <= 0)
        {   /* The first character starts the text. */
            return(0);
        }

        /* The width of everything before it. */
        return(arrRunning[i - 1]);
    }

    /*------------------------------------------------------------------------*/
    /* Vtv_TextWidth:                                                         */
    /*                                                                        */
    /* A text's width as the control lays its labels out (GDI, the control's  */
    /* own font -- Vtv_ControlFont -- on one of its device contexts), for use */
    /* outside painting.                                                      */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sText : the text.                                                  */
    /*                                                                        */
    /* Returns:                                                               */
    /*     int : the width, pixels.                                           */
    /*------------------------------------------------------------------------*/
    int Vtv_TextWidth(string sText)
    {
        IntPtr hdc;                                      // a device context of the control's
        IntPtr hOldFont;                                 // the font it replaced
        int[] arrRunning;                                // running widths (needed by the call)
        VtvSize size;                                    // the text's size

        size.Vsz_cx = 0;
        size.Vsz_cy = 0;
        using (Graphics g = CreateGraphics())
        {
            hdc = g.GetHdc();
            hOldFont = Vtv_SelectObject(hdc, Vtv_ControlFont());
            arrRunning = new int[Math.Max(1, sText.Length)];
            Vtv_GetTextExtentExPoint(hdc, sText, sText.Length, 0, IntPtr.Zero,
                arrRunning, out size);
            Vtv_SelectObject(hdc, hOldFont);
            g.ReleaseHdc(hdc);
        }

        /* Its width. */
        return(size.Vsz_cx);
    }

    /*------------------------------------------------------------------------*/
    /* Vtv_DrawEditKey:                                                       */
    /*                                                                        */
    /* Draws the key part of the label of the item being edited in place      */
    /* (its label holds only that part while the edit box is open; see        */
    /* Vtv_PrepareValueEdit), where the control drew only the selection       */
    /* colour: the label's area is filled with the plain background and the   */
    /* key part drawn in the node's own colour (red for a repeated key), at   */
    /* the margin the control uses for label text, the key itself in its      */
    /* name role's colour when View > Color Keys is on.  So the row reads     */
    /* "key: [value being typed]".                                            */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     hdc       : the device context the control is painting with.       */
    /*     treeNode  : the node being edited.                                 */
    /*     colorBack : its background.                                        */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the key part is drawn.                                      */
    /*------------------------------------------------------------------------*/
    void Vtv_DrawEditKey(IntPtr hdc, TreeNode treeNode, Color colorBack)
    {
        VoorheesTreeTag tag;                             // the node's tag
        int iKeyFrom;                                    // the key run, or -1
        int iKeyLength;                                  // its length

        tag = treeNode.Tag as VoorheesTreeTag;
        iKeyFrom = -1;
        iKeyLength = 0;
        if (Vtv_bColorKeys && treeNode.ForeColor.IsEmpty && tag != null
            && tag.Vtt_iKeyFrom >= 0 && tag.Vtt_iKeyLength > 0
            && tag.Vtt_iKeyFrom + tag.Vtt_iKeyLength <= treeNode.Text.Length)
        {   /* Coloured keys: the key itself in the key colour. */
            iKeyFrom = tag.Vtt_iKeyFrom;
            iKeyLength = tag.Vtt_iKeyLength;
        }

        /* The key part on the plain background, as any coloured label (no   */
        /* value run: the value is in the box, not the label).               */
        Vtv_DrawLabelRuns(hdc, treeNode, colorBack, Vtv_NodeTextColor(treeNode),
            iKeyFrom, iKeyLength, -1, 0, Vtv_NodeTextColor(treeNode), -1);
    }

    /*------------------------------------------------------------------------*/
    /* VtvRect / VtvSize                                                      */
    /*                                                                        */
    /* RECT and SIZE, for the GDI calls below; laid out as in C.              */
    /*------------------------------------------------------------------------*/
    [StructLayout(LayoutKind.Sequential)]
    struct VtvRect
    {
        public int Vrc_iLeft;                            // left edge
        public int Vrc_iTop;                             // top edge
        public int Vrc_iRight;                           // right edge (exclusive)
        public int Vrc_iBottom;                          // bottom edge (exclusive)
    }

    [StructLayout(LayoutKind.Sequential)]
    struct VtvSize
    {
        public int Vsz_cx;                               // width
        public int Vsz_cy;                               // height
    }

    /*------------------------------------------------------------------------*/
    /* GDI (gdi32.dll), imported, for drawing labels exactly as the control   */
    /* does (Vtv_DrawLabelRuns).  Two ExtTextOut imports under two names, one */
    /* with a rectangle and one without, since no name may be overloaded.     */
    /*     Vtv_SelectObject         : put a font into a device context;       */
    /*                                returns the one it replaced.            */
    /*     Vtv_GetTextExtentExPoint : a text's size and the running width     */
    /*                                after each character.                   */
    /*     Vtv_SetTextColor / Vtv_SetBkColor / Vtv_SetBkMode : the text       */
    /*                                colour, background colour and mode;     */
    /*                                each returns the previous one.          */
    /*     Vtv_ExtTextOutFill       : fill a rectangle (ETO_OPAQUE, no text). */
    /*     Vtv_ExtTextOut           : draw text at a point.                   */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     As the Windows functions of the same names.                        */
    /*                                                                        */
    /* Returns:                                                               */
    /*     As the Windows functions of the same names.                        */
    /*------------------------------------------------------------------------*/
    const uint VTV_ETO_OPAQUE = 0x0002;                  // ExtTextOut: fill the rectangle with the background colour
    const int VTV_WM_GETFONT = 0x0031;                   // ask a control for the GDI font it draws with
    const int VTV_TRANSPARENT = 1;                       // SetBkMode: text leaves the background as it is

    [DllImport("gdi32.dll", EntryPoint = "SelectObject")]
    static extern IntPtr Vtv_SelectObject(IntPtr hdc, IntPtr hObj);

    [DllImport("gdi32.dll", EntryPoint = "GetTextExtentExPointW", CharSet = CharSet.Unicode)]
    static extern bool Vtv_GetTextExtentExPoint(IntPtr hdc, string sText, int cchString,
        int nMaxExtent, IntPtr lpnFit, [Out] int[] alpDx, out VtvSize size);

    [DllImport("gdi32.dll", EntryPoint = "SetTextColor")]
    static extern int Vtv_SetTextColor(IntPtr hdc, int crColor);

    [DllImport("gdi32.dll", EntryPoint = "SetBkColor")]
    static extern int Vtv_SetBkColor(IntPtr hdc, int crColor);

    [DllImport("gdi32.dll", EntryPoint = "SetBkMode")]
    static extern int Vtv_SetBkMode(IntPtr hdc, int iMode);

    [DllImport("gdi32.dll", EntryPoint = "ExtTextOutW", CharSet = CharSet.Unicode)]
    static extern bool Vtv_ExtTextOutFill(IntPtr hdc, int x, int y, uint fuOptions,
        ref VtvRect lprc, string sText, int cbCount, IntPtr lpDx);

    [DllImport("gdi32.dll", EntryPoint = "ExtTextOutW", CharSet = CharSet.Unicode)]
    static extern bool Vtv_ExtTextOut(IntPtr hdc, int x, int y, uint fuOptions,
        IntPtr lprc, string sText, int cbCount, IntPtr lpDx);

    /*------------------------------------------------------------------------*/
    /* VtvNmHdr                                                               */
    /*                                                                        */
    /* NMHDR, the header every notification starts with.  It is its own       */
    /* structure (not flattened into VtvCustomDraw) because C pads it to a    */
    /* multiple of the pointer size: on 64-bit Windows it is 24 bytes, so     */
    /* what follows it starts at 24, not straight after the 4-byte code.      */
    /* (Flattened, the draw stage was read from that padding: always 0.)      */
    /*------------------------------------------------------------------------*/
    [StructLayout(LayoutKind.Sequential)]
    struct VtvNmHdr
    {
        public IntPtr Vnh_hwndFrom;                      // the control sending it: the tree
        public IntPtr Vnh_idFrom;                        // its control ID
        public int Vnh_iCode;                            // the notification: NM_CUSTOMDRAW here
    }

    /*------------------------------------------------------------------------*/
    /* VtvCustomDraw                                                          */
    /*                                                                        */
    /* The start of NMTVCUSTOMDRAW (an NMCUSTOMDRAW, which begins with an     */
    /* NMHDR), as far as this control reads it.  Laid out sequentially, the   */
    /* header nested, so the marshaller gives it the same padding as the C    */
    /* structure on both 32- and 64-bit Windows.                              */
    /*------------------------------------------------------------------------*/
    [StructLayout(LayoutKind.Sequential)]
    struct VtvCustomDraw
    {
        public VtvNmHdr Vcd_hdr;                         // NMHDR: who sent it, and the code
        public int Vcd_iDrawStage;                       // dwDrawStage: a CDDS_ value
        public IntPtr Vcd_hdc;                           // hdc: the device context being painted
        public int Vcd_iLeft;                            // rc.left: the item's row, client coordinates
        public int Vcd_iTop;                             // rc.top
        public int Vcd_iRight;                           // rc.right
        public int Vcd_iBottom;                          // rc.bottom
        public IntPtr Vcd_hItem;                         // dwItemSpec: the item (HTREEITEM)
        public int Vcd_iItemState;                       // uItemState: CDIS_ bits
        public IntPtr Vcd_lItemParam;                    // lItemlParam: the item's application value
    }

    /*------------------------------------------------------------------------*/
    /* WndProc:                                                               */
    /*                                                                        */
    /* The control's window procedure.                                        */
    /*     o Custom draw: WinForms answers every stage as usual (it applies   */
    /*       the nodes' colours); an item pre-paint answer also asks for the  */
    /*       item's post-paint stage, at which Vtv_PostPaintItem greys the    */
    /*       label's tail (or, for the item being edited in place, draws its  */
    /*       key part beside the edit box).                                   */
    /*     o Progress: while a populate is being counted, each insert message */
    /*       adds a unit; while a removal is being counted, each item read    */
    /*       and each delete notification adds one (and the notification is   */
    /*       answered here, as nothing else uses it).                         */
    /* Every other message, counted or not, goes on to the normal handling.   */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     m : the message.                                                   */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the message is handled.                                     */
    /*------------------------------------------------------------------------*/
    protected override void WndProc(ref Message m)
    {
        int iCode;                                       // a notification's code, from its NMHDR
        VtvCustomDraw cd;                                // a custom draw notification's contents

        if (m.Msg == VTV_WM_REFLECT_NOTIFY)
        {   /* A notification: custom draw is handled here.                  */
            /* NMHDR is { HWND hwndFrom; UINT_PTR idFrom; UINT code }: the   */
            /* code follows two pointer-sized fields.                        */
            iCode = Marshal.ReadInt32(m.LParam, IntPtr.Size * 2);
            if (iCode == VTV_NM_CUSTOMDRAW)
            {   /* Custom draw: see which stage. */
                cd = (VtvCustomDraw)Marshal.PtrToStructure(m.LParam,
                    typeof(VtvCustomDraw));
                if (cd.Vcd_iDrawStage == VTV_CDDS_ITEMPOSTPAINT)
                {   /* An item has been painted: grey its tail, if it has one. */
                    Vtv_PostPaintItem(cd.Vcd_hdc, cd.Vcd_hItem,
                        Rectangle.FromLTRB(cd.Vcd_iLeft, cd.Vcd_iTop,
                        cd.Vcd_iRight, cd.Vcd_iBottom), cd.Vcd_iItemState);
                    m.Result = IntPtr.Zero;
                    return;
                }

                base.WndProc(ref m);
                if (cd.Vcd_iDrawStage == VTV_CDDS_ITEMPREPAINT)
                {   /* An item is about to be painted: ask to hear when it has been. */
                    m.Result = (IntPtr)(m.Result.ToInt64() | VTV_CDRF_NOTIFYPOSTPAINT);
                }
                return;
            }
        }

        if (Vtv_bCountDeletes && m.Msg == VTV_WM_REFLECT_NOTIFY)
        {   /* A notification while removing: see whether it is a deletion.   */
            /* NMHDR is { HWND hwndFrom; UINT_PTR idFrom; UINT code }: the    */
            /* code follows two pointer-sized fields.                         */
            iCode = Marshal.ReadInt32(m.LParam, IntPtr.Size * 2);
            if (iCode == VTV_TVN_DELETEITEMW || iCode == VTV_TVN_DELETEITEMA)
            {   /* One item gone: count it, and answer here. */
                Vtv_CountUnit();
                m.Result = IntPtr.Zero;
                return;
            }
        }

        if (Vtv_bCountDeletes
            && (m.Msg == VTV_TVM_GETITEMW || m.Msg == VTV_TVM_GETITEMA))
        {   /* WinForms reads each node once while it forgets it: count it. */
            Vtv_CountUnit();
        }

        if (Vtv_bCountInserts
            && (m.Msg == VTV_TVM_INSERTITEMW || m.Msg == VTV_TVM_INSERTITEMA))
        {   /* An item going in: count it. */
            Vtv_CountUnit();
        }

        base.WndProc(ref m);
    }

    /*------------------------------------------------------------------------*/
    /* Vtv_CountUnit:                                                         */
    /*                                                                        */
    /* Adds one unit of progress and, every VTV_PROGRESSSTEP units, moves the */
    /* bar.  Only moves it: this runs inside the control's own call, where    */
    /* making the window APPEAR would send the tree paint and size messages   */
    /* half way through, so the window is put up before the call or not at    */
    /* all.                                                                   */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the count advances and the bar may move.                    */
    /*------------------------------------------------------------------------*/
    void Vtv_CountUnit()
    {
        Vtv_lDone++;
        if (Vtv_prog != null && Vtv_lDone % VTV_PROGRESSSTEP == 0)
        {   /* Time for a report. */
            Vtv_prog.BMF_UpdateUiProgress(Vtv_lDone, Vtv_lTotal);
        }
    }
}

/*----------------------------------------------------------------------------*/
/* VoorheesTreeEditShifter                                                    */
/*                                                                            */
/* Watches the tree's in-place edit box and keeps it to the right of the      */
/* label's key part.  The tree control places the box over the label's text   */
/* when the edit starts, and again each time the text changes (the box        */
/* widens with what is typed), always from the label's left edge.  Each time, */
/* the move is caught before it happens (WM_WINDOWPOSCHANGING) and the box's  */
/* new left edge is moved right by the key part's width.  A move to where the */
/* box already is after shifting is not shifted twice.                        */
/*----------------------------------------------------------------------------*/
class VoorheesTreeEditShifter : NativeWindow
{
    const int VTES_WM_WINDOWPOSCHANGING = 0x0046;        // a window is about to be moved or sized; lParam is a WINDOWPOS
    const uint VTES_SWP_NOMOVE = 0x0002;                 // WINDOWPOS flag: the position is not changing

    int Vtes_iShift;                                     // how far right to move the box, pixels
    int Vtes_iLastX;                                     // the left edge it was last moved to (int.MinValue: none yet)

    /*------------------------------------------------------------------------*/
    /* VtesWindowPos                                                          */
    /*                                                                        */
    /* WINDOWPOS, as passed with WM_WINDOWPOSCHANGING; laid out sequentially  */
    /* as in C.                                                               */
    /*------------------------------------------------------------------------*/
    [StructLayout(LayoutKind.Sequential)]
    struct VtesWindowPos
    {
        public IntPtr Vwp_hwnd;                          // the window being moved
        public IntPtr Vwp_hwndInsertAfter;               // its new place in the z-order
        public int Vwp_x;                                // new left edge, parent client coordinates
        public int Vwp_y;                                // new top edge
        public int Vwp_cx;                               // new width
        public int Vwp_cy;                               // new height
        public uint Vwp_uFlags;                          // SWP_ flags
    }

    /*------------------------------------------------------------------------*/
    /* Vtes_Create:                                                           */
    /*                                                                        */
    /* Starts watching an edit box.                                           */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     hEdit  : the tree's edit box.                                      */
    /*     iShift : how far right it goes, pixels.                            */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesTreeEditShifter : the watcher, attached to the box until   */
    /*                               ReleaseHandle or the box is destroyed.   */
    /*------------------------------------------------------------------------*/
    public static VoorheesTreeEditShifter Vtes_Create(IntPtr hEdit, int iShift)
    {
        VoorheesTreeEditShifter shifter;                 // the new watcher

        shifter = new VoorheesTreeEditShifter();
        shifter.Vtes_iShift = iShift;
        shifter.Vtes_iLastX = int.MinValue;
        shifter.AssignHandle(hEdit);

        /* Watching. */
        return(shifter);
    }

    /*------------------------------------------------------------------------*/
    /* WndProc:                                                               */
    /*                                                                        */
    /* The edit box's messages pass through here first.  A move the tree is   */
    /* about to make has its left edge shifted right; every message then      */
    /* goes on to the edit box as normal.                                     */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     m : the message.                                                   */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the message is handled by the edit box.                     */
    /*------------------------------------------------------------------------*/
    protected override void WndProc(ref Message m)
    {
        VtesWindowPos wp;                                // the move about to happen

        if (m.Msg == VTES_WM_WINDOWPOSCHANGING && m.LParam != IntPtr.Zero)
        {   /* A move or resize: see whether the position changes. */
            wp = (VtesWindowPos)Marshal.PtrToStructure(m.LParam, typeof(VtesWindowPos));
            if ((wp.Vwp_uFlags & VTES_SWP_NOMOVE) == 0 && wp.Vwp_x != Vtes_iLastX)
            {   /* Placed from the label's left edge: after the key part instead. */
                wp.Vwp_x += Vtes_iShift;
                Vtes_iLastX = wp.Vwp_x;
                Marshal.StructureToPtr(wp, m.LParam, false);
            }
        }

        base.WndProc(ref m);
    }
}
