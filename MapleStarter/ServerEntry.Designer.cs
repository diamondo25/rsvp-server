﻿namespace MapleStarter
{
    partial class ServerEntry
    {
        /// <summary> 
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary> 
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Component Designer generated code

        /// <summary> 
        /// Required method for Designer support - do not modify 
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            label1 = new Label();
            label2 = new Label();
            lblIP = new Label();
            lblMachineName = new Label();
            btnStart = new Button();
            btnShortcut = new Button();
            SuspendLayout();
            // 
            // label1
            // 
            label1.AutoSize = true;
            label1.Location = new Point(3, 19);
            label1.Name = "label1";
            label1.Size = new Size(80, 20);
            label1.TabIndex = 0;
            label1.Text = "Hosted by:";
            // 
            // label2
            // 
            label2.AutoSize = true;
            label2.Location = new Point(3, 39);
            label2.Name = "label2";
            label2.Size = new Size(69, 20);
            label2.TabIndex = 1;
            label2.Text = "Server IP:";
            // 
            // lblIP
            // 
            lblIP.AutoSize = true;
            lblIP.Location = new Point(83, 39);
            lblIP.Name = "lblIP";
            lblIP.Size = new Size(50, 20);
            lblIP.TabIndex = 2;
            lblIP.Text = "label3";
            // 
            // lblMachineName
            // 
            lblMachineName.AutoSize = true;
            lblMachineName.Location = new Point(83, 19);
            lblMachineName.Name = "lblMachineName";
            lblMachineName.Size = new Size(50, 20);
            lblMachineName.TabIndex = 3;
            lblMachineName.Text = "label3";
            // 
            // btnStart
            // 
            btnStart.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Right;
            btnStart.Location = new Point(284, 19);
            btnStart.Margin = new Padding(3, 4, 3, 4);
            btnStart.Name = "btnStart";
            btnStart.Size = new Size(86, 40);
            btnStart.TabIndex = 4;
            btnStart.Text = "Start!";
            btnStart.UseVisualStyleBackColor = true;
            btnStart.Click += btnStart_Click;
            // 
            // btnShortcut
            // 
            btnShortcut.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Right;
            btnShortcut.Location = new Point(376, 19);
            btnShortcut.Margin = new Padding(3, 4, 3, 4);
            btnShortcut.Name = "btnShortcut";
            btnShortcut.Size = new Size(86, 40);
            btnShortcut.TabIndex = 5;
            btnShortcut.Text = "Save";
            btnShortcut.UseVisualStyleBackColor = true;
            btnShortcut.Click += btnShortcut_Click;
            // 
            // ServerEntry
            // 
            AutoScaleDimensions = new SizeF(8F, 20F);
            AutoScaleMode = AutoScaleMode.Font;
            AutoSize = true;
            Controls.Add(btnShortcut);
            Controls.Add(btnStart);
            Controls.Add(lblMachineName);
            Controls.Add(lblIP);
            Controls.Add(label2);
            Controls.Add(label1);
            Margin = new Padding(3, 4, 3, 4);
            MaximumSize = new Size(0, 79);
            MinimumSize = new Size(465, 79);
            Name = "ServerEntry";
            Size = new Size(465, 79);
            Load += ServerEntry_Load;
            ResumeLayout(false);
            PerformLayout();
        }

        #endregion

        private Label label1;
        private Label label2;
        private Label lblIP;
        private Label lblMachineName;
        private Button btnStart;
        private Button btnShortcut;
    }
}
