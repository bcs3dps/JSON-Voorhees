/*-----------------------------------------------------------------------------*/
/* BusyModalForm.cs                                                            */
/*                                                                             */
/* SPDX-License-Identifier: GPL-2.0-or-later                                   */
/* Copyright (c) 2026 B. C. Services                                           */
/*                                                                             */
/*-----------------------------------------------------------------------------*/
/* A "please wait" modal for work that blocks the user for longer than a       */
/* moment.  The work runs on a BackgroundWorker while the dialog shows a       */
/* title, a line of explanation and a marquee progress bar; the dialog         */
/* closes itself when the work finishes.  There is no Cancel button and no     */
/* close box: it is a window into work that has to finish anyway, not a       */
/* control over it.  An exception thrown by the work is caught and handed      */
/* back to the caller as Run's return value.                                   */
/*                                                                             */
/* SHOW DELAY.  Run waits up to a given number of milliseconds for the work    */
/* before showing anything, so an operation that finishes quickly never        */
/* flashes a dialog, and one that does not shows it.  A delay of 0 shows the   */
/* dialog at once.                                                             */
/*                                                                             */
/* UI-THREAD WORK.  Some slow work cannot move to a worker thread at all:      */
/* filling a TreeView or ListView, for one, must happen on the thread that     */
/* owns the control.  For that there is a second mode with a measured bar:     */
/*     BusyModalForm prog = BusyModalForm.BMF_BeginUiProgress(this,            */
/*         "Drawing the tree", "big.json", "Voorhees", 300);                   */
/*     try { ... prog.BMF_ReportUiProgress(lDone, lTotal); ... }               */
/*     finally { prog.BMF_EndUiProgress(); }                                   */
/* The window appears (modeless, so the caller keeps running) the first time   */
/* a report arrives after the delay, or at once with BMF_ShowNow, and is       */
/* painted directly on every report.  BMF_UpdateUiProgress moves the bar but   */
/* never makes the window appear, for reports made from inside another        */
/* control's own long call.  No messages are dispatched while the work runs,   */
/* so nothing the user does can start other work part way through; a          */
/* PeekMessage that removes nothing tells Windows the program is still alive, */
/* which stops it greying the windows out as "Not Responding" after five       */
/* seconds.                                                                    */
/*                                                                             */
/* Usage:                                                                      */
/*     Exception err = BusyModalForm.Run(this, "Opening", "big.json",          */
/*         delegate { ReadAndParse(); }, "Voorhees", 250);                     */
/*     if (err != null) { ...report it... }                                    */
/*                                                                             */
/* The work delegate runs on a worker thread, so it must not touch any         */
/* control: it does its computing, stores its results in fields or captured    */
/* locals, and the caller uses them on the UI thread once Run returns.  Run    */
/* returns only when the work has finished, so there is never anything         */
/* still running behind the caller's back.                                     */
/*                                                                             */
/* Features of this version: the show delay above, and with it the single      */
/* Run function taking the caption and delay; the UI-thread mode above         */
/* (BMF_BeginUiProgress and friends); a Resize handler that lays the dialog    */
/* out from its fonts.                                                         */
/*-----------------------------------------------------------------------------*/

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace Voorhees.Forms
{
    /*-------------------------------------------------------------------------*/
    /* BusyModalForm                                                           */
    /*                                                                         */
    /* Created and shown only by Run.  The constructor is private so the       */
    /* form cannot be shown without its work.                                  */
    /*-------------------------------------------------------------------------*/
    public class BusyModalForm : Form
    {
        /*---------------------------------------------------------------------*/
        /* Caption used when Run is given none.  A program sets it once        */
        /* (for example to its own name) rather than passing it every call.    */
        /*---------------------------------------------------------------------*/

        public static string DefaultFormCaption = "Working";

        /*---------------------------------------------------------------------*/
        /* Layout padding, pixels.  Everything else is measured from the       */
        /* fonts and the label text.                                           */
        /*---------------------------------------------------------------------*/

        const int BMF_PAD = 16;                          // space round the content and between rows
        const int BMF_MINBARWIDTH = 260;                 // narrowest progress bar, chosen to read as a bar

        /*---------------------------------------------------------------------*/
        /* UI-thread mode: the bar runs 0..BMF_BARMAX, so a step is a tenth    */
        /* of a percent and the bar moves smoothly on any width.               */
        /*---------------------------------------------------------------------*/

        const int BMF_BARMAX = 1000;                     // ProgressBar.Maximum in UI-thread mode
        const uint BMF_PM_NOREMOVE = 0x0000;             // PeekMessage: look, but leave the message queued

        Label Bm_lblTitle;                               // bold first line: what is happening
        Label Bm_lblBody;                                // second line: what it is happening to
        ProgressBar Bm_prgBar;                           // marquee bar, or measured bar in UI-thread mode
        BackgroundWorker Bm_bwWorker;                    // runs the work off the UI thread
        Action Bm_work;                                  // the caller's work
        Exception Bm_exError;                            // what the work threw, or null
        ManualResetEvent Bm_evDone;                      // set by the worker when the work has finished
        bool Bm_bShown;                                  // the dialog has appeared on screen
        Form Bm_frmOwner;                                // UI-thread mode: window to centre on
        Stopwatch Bm_swElapsed;                          // UI-thread mode: time since BMF_BeginUiProgress
        int Bm_iShowDelayMs;                             // UI-thread mode: when to appear
        int Bm_iBarValue;                                // UI-thread mode: value last painted

        /*---------------------------------------------------------------------*/
        /* BMF_MSG: the Win32 MSG structure, for PeekMessage to fill in.       */
        /* Its contents are never read.                                        */
        /*---------------------------------------------------------------------*/

        [StructLayout(LayoutKind.Sequential)]
        struct BMF_MSG
        {
            public IntPtr hwnd;                          // window the message is for
            public uint message;                         // message number
            public IntPtr wParam;                        // first parameter
            public IntPtr lParam;                        // second parameter
            public uint time;                            // when it was posted
            public int ptX;                              // cursor x when it was posted
            public int ptY;                              // cursor y when it was posted
        }

        /*---------------------------------------------------------------------*/
        /* PeekMessage:                                                        */
        /*                                                                     */
        /* user32.dll, imported.  Looks at the thread's message queue.  Used   */
        /* with BMF_PM_NOREMOVE only, so nothing is taken off the queue or     */
        /* dispatched; the call itself is what tells Windows the thread is     */
        /* alive, which keeps its windows from being shown as "Not             */
        /* Responding" during long UI-thread work.  Like every message wait,   */
        /* it does deliver messages SENT from other threads.                   */
        /*                                                                     */
        /* Arguments:                                                          */
        /*     lpMsg         : receives the message looked at; not used.       */
        /*     hWnd          : IntPtr.Zero for any window of this thread.      */
        /*     wMsgFilterMin : 0, no filter.                                   */
        /*     wMsgFilterMax : 0, no filter.                                   */
        /*     wRemoveMsg    : BMF_PM_NOREMOVE.                                */
        /*                                                                     */
        /* Returns:                                                            */
        /*     bool : true if a message is waiting; not used.                  */
        /*---------------------------------------------------------------------*/
        [DllImport("user32.dll")]
        static extern bool PeekMessage(out BMF_MSG lpMsg, IntPtr hWnd,
            uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

        /*---------------------------------------------------------------------*/
        /* Error:                                                              */
        /*                                                                     */
        /* The exception the work threw, or null when it finished normally.    */
        /* Only meaningful once Run has returned.                              */
        /*                                                                     */
        /* Arguments:                                                          */
        /*     None.                                                           */
        /*                                                                     */
        /* Returns:                                                            */
        /*     Exception : the work's exception, or null.                      */
        /*---------------------------------------------------------------------*/
        public Exception Error
        {
            get { return(Bm_exError); }
        }

        /*---------------------------------------------------------------------*/
        /* BusyModalForm:                                                      */
        /*                                                                     */
        /* Builds the dialog without showing it.                               */
        /*                                                                     */
        /* Arguments:                                                          */
        /*     sTitle   : the bold first line.                                 */
        /*     sBody    : the second line; null shows nothing there.           */
        /*     work     : the work to run.                                     */
        /*     sCaption : window caption; null or empty for                    */
        /*                DefaultFormCaption.                                  */
        /*                                                                     */
        /* Returns:                                                            */
        /*     BusyModalForm : the dialog, not yet shown.                      */
        /*---------------------------------------------------------------------*/
        BusyModalForm(string sTitle, string sBody, Action work,
            string sCaption)
        {
            Bm_work = work;
            Bm_exError = null;
            Bm_bShown = false;
            Bm_evDone = new ManualResetEvent(false);

            if (string.IsNullOrEmpty(sCaption))
            {   /* No caption given: use the program-wide default. */
                Text = DefaultFormCaption;
            }
            else
            {   /* Caller's own caption. */
                Text = sCaption;
            }

            BMF_BuildUi(sTitle, sBody);
        }

        /*---------------------------------------------------------------------*/
        /* Run:                                                                */
        /*                                                                     */
        /* Runs the work on a BackgroundWorker and returns when it has         */
        /* finished.  The UI thread first waits up to iShowDelayMs for it:     */
        /* work that is done by then returns straight away and no dialog is    */
        /* ever shown.  Otherwise the dialog is shown modally (centred on      */
        /* the owner) until the work finishes and the dialog closes itself.    */
        /*                                                                     */
        /* Arguments:                                                          */
        /*     owner        : window the dialog is centred on and modal to.    */
        /*     sTitle       : the bold first line, e.g. "Opening file".        */
        /*     sBody        : the second line, e.g. the file name.             */
        /*     work         : the work; runs on a worker thread and must not   */
        /*                    touch controls.  null does nothing.              */
        /*     sCaption     : window caption; null or empty for                */
        /*                    DefaultFormCaption.                              */
        /*     iShowDelayMs : how long to wait before showing the dialog,      */
        /*                    milliseconds; 0 shows it at once.                */
        /*                                                                     */
        /* Returns:                                                            */
        /*     Exception : what the work threw, or null when it finished       */
        /*                 normally.                                           */
        /*---------------------------------------------------------------------*/
        public static Exception Run(Form owner, string sTitle, string sBody,
            Action work, string sCaption, int iShowDelayMs)
        {
            BusyModalForm dlg;                           // the dialog, shown only if the work is slow
            Exception exResult;                          // what the work threw

            if (work == null)
            {   /* Nothing to run: nothing can go wrong. */
                return(null);
            }

            dlg = new BusyModalForm(sTitle, sBody, work, sCaption);
            using (dlg)
            {
                /* The worker is created here, on the UI thread, so its   */
                /* completion event is delivered back to this thread.    */
                dlg.Bm_bwWorker = new BackgroundWorker();
                dlg.Bm_bwWorker.DoWork += dlg.BMF_WorkerDoWork;
                dlg.Bm_bwWorker.RunWorkerCompleted += dlg.BMF_WorkerCompleted;
                dlg.Bm_bwWorker.RunWorkerAsync();

                if (!dlg.Bm_evDone.WaitOne(Math.Max(0, iShowDelayMs)))
                {   /* Still running after the delay: show the dialog until it finishes. */
                    dlg.ShowDialog(owner);
                }

                /* ShowDialog returns only once the work is done, and the */
                /* quick path above returns only once the event was set,  */
                /* so the work has finished either way.                   */
                exResult = dlg.Bm_exError;
                dlg.Bm_evDone.Close();
            }

            /* The work's exception, or null. */
            return(exResult);
        }

        /*---------------------------------------------------------------------*/
        /* BMF_BeginUiProgress:                                                */
        /*                                                                     */
        /* Starts the UI-thread mode, for slow work that has to run on the     */
        /* UI thread itself.  Nothing is shown yet: the window appears from    */
        /* BMF_ReportUiProgress once iShowDelayMs has passed, so quick work    */
        /* never shows it.  The caller must end with BMF_EndUiProgress, in a   */
        /* finally block, so the window cannot be left behind by an            */
        /* exception.                                                          */
        /*                                                                     */
        /* Arguments:                                                          */
        /*     owner        : window the progress is centred on; it does not   */
        /*                    become the owner (a modeless window is kept      */
        /*                    ownerless), only the place to appear.            */
        /*     sTitle       : the bold first line.                             */
        /*     sBody        : the second line, or null.                        */
        /*     sCaption     : window caption; null or empty for                */
        /*                    DefaultFormCaption.                              */
        /*     iShowDelayMs : how long the work may run before the window      */
        /*                    appears, milliseconds.                           */
        /*                                                                     */
        /* Returns:                                                            */
        /*     BusyModalForm : the progress object, for the Report and End     */
        /*                     calls.                                          */
        /*---------------------------------------------------------------------*/
        public static BusyModalForm BMF_BeginUiProgress(Form owner,
            string sTitle, string sBody, string sCaption, int iShowDelayMs)
        {
            BusyModalForm prog;                          // the progress window, hidden for now

            prog = new BusyModalForm(sTitle, sBody, null, sCaption);
            prog.Bm_frmOwner = owner;
            prog.Bm_iShowDelayMs = Math.Max(0, iShowDelayMs);
            prog.Bm_iBarValue = -1;
            prog.Bm_swElapsed = Stopwatch.StartNew();

            /* A measured bar, not a marquee: a marquee animates on a     */
            /* timer, which needs the message loop this mode never runs.  */
            prog.Bm_prgBar.Style = ProgressBarStyle.Continuous;
            prog.Bm_prgBar.Minimum = 0;
            prog.Bm_prgBar.Maximum = BMF_BARMAX;

            /* Ready; it shows itself when the work turns out to be slow. */
            return(prog);
        }

        /*---------------------------------------------------------------------*/
        /* BMF_ReportUiProgress:                                               */
        /*                                                                     */
        /* Called from the UI-thread work, as often as is convenient; it is    */
        /* cheap when nothing visible changes.                                 */
        /*     o Before the show delay has passed: nothing.                    */
        /*     o The first time after it: the window appears (BMF_ShowNow).    */
        /*     o Then the bar moves (BMF_UpdateUiProgress), which also keeps   */
        /*       Windows from marking the program as hung.                     */
        /* Because it can make the window appear, call it only between steps   */
        /* of the caller's own work, never from inside another control's       */
        /* call; use BMF_UpdateUiProgress there.                               */
        /*                                                                     */
        /* Arguments:                                                          */
        /*     lDone  : units of work finished so far.                         */
        /*     lTotal : units of work in all; 0 or less shows an empty bar.    */
        /*                                                                     */
        /* Returns:                                                            */
        /*     void : the window may appear or its bar move.                   */
        /*---------------------------------------------------------------------*/
        public void BMF_ReportUiProgress(long lDone, long lTotal)
        {
            if (!Bm_bShown && Bm_swElapsed.ElapsedMilliseconds < Bm_iShowDelayMs)
            {   /* Still quick work: stay hidden. */
                return;
            }

            /* Slow enough to show: appear (once), then move the bar. */
            BMF_ShowNow();
            BMF_UpdateUiProgress(lDone, lTotal);
        }

        /*---------------------------------------------------------------------*/
        /* BMF_ShowNow:                                                        */
        /*                                                                     */
        /* Makes the UI-thread progress window appear now, whatever the show   */
        /* delay, centred on the owner and painted at once.  Does nothing if   */
        /* it is already showing.  For a caller that knows in advance that     */
        /* its work will be slow, and that is about to start a long call       */
        /* inside which the window must not appear (see                        */
        /* BMF_UpdateUiProgress).                                              */
        /*                                                                     */
        /* Arguments:                                                          */
        /*     None.                                                           */
        /*                                                                     */
        /* Returns:                                                            */
        /*     void : the window is on screen.                                 */
        /*---------------------------------------------------------------------*/
        public void BMF_ShowNow()
        {
            Rectangle rcOwner;                           // owner's bounds, to centre on

            if (Bm_bShown)
            {   /* Already on screen. */
                return;
            }

            StartPosition = FormStartPosition.Manual;
            if (Bm_frmOwner != null)
            {   /* Centre on the owner window. */
                rcOwner = Bm_frmOwner.Bounds;
            }
            else
            {   /* No owner given: centre on the screen. */
                rcOwner = Screen.PrimaryScreen.WorkingArea;
            }
            Location = new Point(
                rcOwner.Left + (rcOwner.Width - Width) / 2,
                rcOwner.Top + (rcOwner.Height - Height) / 2);

            /* Show it and paint it straight away: no message loop will   */
            /* run to paint it later.                                     */
            Show();
            Bm_bShown = true;
            Refresh();
        }

        /*---------------------------------------------------------------------*/
        /* BMF_UpdateUiProgress:                                               */
        /*                                                                     */
        /* Moves the bar of a progress window that is ALREADY showing, and     */
        /* peeks at the message queue (see PeekMessage) so Windows does not    */
        /* mark the program as hung.  It never makes the window appear, so it  */
        /* is safe to call from inside another control's long-running call:    */
        /* the only window it paints is the progress bar.  Before the window   */
        /* is shown it does nothing.                                           */
        /*                                                                     */
        /* Arguments:                                                          */
        /*     lDone  : units of work finished so far.                         */
        /*     lTotal : units of work in all; 0 or less shows an empty bar.    */
        /*                                                                     */
        /* Returns:                                                            */
        /*     void : the bar may move.                                        */
        /*---------------------------------------------------------------------*/
        public void BMF_UpdateUiProgress(long lDone, long lTotal)
        {
            BMF_MSG msg;                                 // filled by PeekMessage; not used
            int iValue;                                  // bar value for this report

            if (!Bm_bShown)
            {   /* Not on screen: nothing to move. */
                return;
            }

            /* Keep Windows from treating the thread as hung. */
            PeekMessage(out msg, IntPtr.Zero, 0, 0, BMF_PM_NOREMOVE);

            /* The bar value for this much of the total. */
            if (lTotal > 0)
            {   /* Measurable: scale to the bar, clamped to its range. */
                iValue = (int)Math.Min((long)BMF_BARMAX,
                    Math.Max(0L, lDone * BMF_BARMAX / lTotal));
            }
            else
            {   /* Nothing to measure against: empty bar. */
                iValue = 0;
            }

            if (iValue != Bm_iBarValue)
            {   /* Visible change: move the bar and paint it now. */
                Bm_iBarValue = iValue;

                /* A themed bar glides towards a higher value on a timer   */
                /* this mode never lets run.  Going one past and then back */
                /* makes it draw the value straight away instead.          */
                if (iValue < BMF_BARMAX)
                {   /* Room above: overshoot by one, then settle. */
                    Bm_prgBar.Value = iValue + 1;
                    Bm_prgBar.Value = iValue;
                }
                else
                {   /* At the top: the full bar is drawn at once anyway. */
                    Bm_prgBar.Value = iValue;
                }
                Bm_prgBar.Update();
            }
        }

        /*---------------------------------------------------------------------*/
        /* BMF_EndUiProgress:                                                  */
        /*                                                                     */
        /* Ends the UI-thread mode: closes the window if it appeared and       */
        /* releases it either way.  Safe to call more than once.               */
        /*                                                                     */
        /* Arguments:                                                          */
        /*     None.                                                           */
        /*                                                                     */
        /* Returns:                                                            */
        /*     void : the window is gone.                                      */
        /*---------------------------------------------------------------------*/
        public void BMF_EndUiProgress()
        {
            if (IsDisposed)
            {   /* Already ended. */
                return;
            }

            if (Bm_bShown)
            {   /* It appeared: take it down. */
                Close();
            }

            Bm_evDone.Close();
            Dispose();
        }

        /*---------------------------------------------------------------------*/
        /* BMF_BuildUi:                                                        */
        /*                                                                     */
        /* Creates the two labels and the progress bar and sets the window     */
        /* style: a fixed dialog with no close, minimize or maximize box,      */
        /* not in the taskbar, centred on its owner.  Positions and the        */
        /* window size are worked out by BMF_Layout from the fonts and the     */
        /* label text, here and again on every resize.                         */
        /*                                                                     */
        /* Arguments:                                                          */
        /*     sTitle : the bold first line.                                   */
        /*     sBody  : the second line, or null.                              */
        /*                                                                     */
        /* Returns:                                                            */
        /*     void : the controls exist and the window is sized.              */
        /*---------------------------------------------------------------------*/
        void BMF_BuildUi(string sTitle, string sBody)
        {
            FormBorderStyle = FormBorderStyle.FixedDialog;
            ControlBox = false;                          // no close box: the work has to finish
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            ShowIcon = false;
            StartPosition = FormStartPosition.CenterParent;
            Font = SystemFonts.MessageBoxFont;

            /* Title: the message font, bold and a point larger. */
            Bm_lblTitle = new Label();
            Bm_lblTitle.Text = sTitle;
            Bm_lblTitle.AutoSize = true;
            Bm_lblTitle.Font = new Font(Font.FontFamily,
                Font.SizeInPoints + 1f, FontStyle.Bold);
            Controls.Add(Bm_lblTitle);

            /* Body: plain text, empty when none was given. */
            Bm_lblBody = new Label();
            if (sBody == null)
            {   /* Nothing to say under the title. */
                Bm_lblBody.Text = "";
            }
            else
            {   /* The caller's explanation. */
                Bm_lblBody.Text = sBody;
            }
            Bm_lblBody.AutoSize = true;
            Controls.Add(Bm_lblBody);

            /* The bar moves on its own (marquee): the work reports no   */
            /* progress, only that it is still going.                    */
            Bm_prgBar = new ProgressBar();
            Bm_prgBar.Style = ProgressBarStyle.Marquee;
            Bm_prgBar.MarqueeAnimationSpeed = 30;
            Controls.Add(Bm_prgBar);

            /* Lay out now and whenever the size changes; then show it   */
            /* is running only once it is on screen.                     */
            BMF_Layout();
            Resize += delegate { BMF_Layout(); };
            Shown += BMF_Shown;
        }

        /*---------------------------------------------------------------------*/
        /* BMF_Layout:                                                         */
        /*                                                                     */
        /* Places the controls from their measured sizes: the title at the     */
        /* top left, the body under it, the bar under that, as wide as the     */
        /* wider label (never narrower than BMF_MINBARWIDTH) and as tall as    */
        /* a line of text.  The window is then sized to fit with BMF_PAD all   */
        /* round.  PreferredSize is used rather than Width and Height because  */
        /* an AutoSize label has not settled its size this early.              */
        /*                                                                     */
        /* Arguments:                                                          */
        /*     None.                                                           */
        /*                                                                     */
        /* Returns:                                                            */
        /*     void : the controls are placed and ClientSize set.              */
        /*---------------------------------------------------------------------*/
        void BMF_Layout()
        {
            Size szTitle;                                // measured title size
            Size szBody;                                 // measured body size
            Size szClient;                               // window client size needed
            int iContentWidth;                           // width of the widest row
            int iBarHeight;                              // bar height: one line of text plus a little

            if (Bm_prgBar == null)
            {   /* A resize raised while the controls are still being built. */
                return;
            }

            szTitle = Bm_lblTitle.PreferredSize;
            szBody = Bm_lblBody.PreferredSize;
            iContentWidth = Math.Max(BMF_MINBARWIDTH,
                Math.Max(szTitle.Width, szBody.Width));
            iBarHeight = Font.Height + BMF_PAD / 4;

            /* Rows top to bottom, half padding between the two labels. */
            Bm_lblTitle.Location = new Point(BMF_PAD, BMF_PAD);
            Bm_lblBody.Location = new Point(BMF_PAD,
                Bm_lblTitle.Top + szTitle.Height + BMF_PAD / 2);
            Bm_prgBar.SetBounds(BMF_PAD,
                Bm_lblBody.Top + szBody.Height + BMF_PAD,
                iContentWidth, iBarHeight);

            /* The window wraps the content.  Only set when it differs,  */
            /* so the Resize this raises does not loop.                  */
            szClient = new Size(iContentWidth + BMF_PAD * 2,
                Bm_prgBar.Bottom + BMF_PAD);
            if (ClientSize != szClient)
            {   /* Not yet the right size: set it. */
                ClientSize = szClient;
            }
        }

        /*---------------------------------------------------------------------*/
        /* BMF_Shown:                                                          */
        /*                                                                     */
        /* The dialog has appeared.  If the work finished while it was         */
        /* being created, its completion event may already have been           */
        /* handled before Bm_bShown was set, so it is checked here and the     */
        /* dialog closed straight away.                                        */
        /*                                                                     */
        /* Arguments:                                                          */
        /*     sender : this dialog.                                           */
        /*     e      : unused.                                                */
        /*                                                                     */
        /* Returns:                                                            */
        /*     void : the dialog is marked shown, and may close.               */
        /*---------------------------------------------------------------------*/
        void BMF_Shown(object sender, EventArgs e)
        {
            Bm_bShown = true;

            if (Bm_evDone.WaitOne(0))
            {   /* Already finished: nothing to wait for. */
                Close();
            }
        }

        /*---------------------------------------------------------------------*/
        /* BMF_WorkerDoWork:                                                   */
        /*                                                                     */
        /* Worker thread.  Runs the caller's work, keeps any exception it      */
        /* throws for Run to return, and sets Bm_evDone last, whether the      */
        /* work succeeded or not, so Run's wait always ends.                   */
        /*                                                                     */
        /* Arguments:                                                          */
        /*     sender : the BackgroundWorker.                                  */
        /*     e      : unused; the result travels in Bm_exError.              */
        /*                                                                     */
        /* Returns:                                                            */
        /*     void : the work has run.                                        */
        /*---------------------------------------------------------------------*/
        void BMF_WorkerDoWork(object sender, DoWorkEventArgs e)
        {
            try
            {
                Bm_work();
            }
            catch (Exception ex)
            {   /* The work failed: keep why, for the caller. */
                Bm_exError = ex;
            }
            finally
            {
                Bm_evDone.Set();
            }
        }

        /*---------------------------------------------------------------------*/
        /* BMF_WorkerCompleted:                                                */
        /*                                                                     */
        /* UI thread, delivered after the work has finished.  Closes the       */
        /* dialog if it is showing.  On the quick path the dialog never        */
        /* appeared and Run has already returned, so there is nothing to do;   */
        /* this handler then only reads a field, never the window.             */
        /*                                                                     */
        /* Arguments:                                                          */
        /*     sender : the BackgroundWorker.                                  */
        /*     e      : unused; errors were caught in BMF_WorkerDoWork.        */
        /*                                                                     */
        /* Returns:                                                            */
        /*     void : the dialog may close.                                    */
        /*---------------------------------------------------------------------*/
        void BMF_WorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            if (Bm_bShown && !IsDisposed)
            {   /* On screen: the work is done, so take it down. */
                Close();
            }
        }
    }
}
