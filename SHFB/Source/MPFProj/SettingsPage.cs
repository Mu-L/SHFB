/********************************************************************************************

Copyright (c) Microsoft Corporation 
All rights reserved. 

Microsoft Public License: 

This license governs use of the accompanying software. If you use the software, you 
accept this license. If you do not accept the license, do not use the software. 

1. Definitions 
The terms "reproduce," "reproduction," "derivative works," and "distribution" have the 
same meaning here as under U.S. copyright law. 
A "contribution" is the original software, or any additions or changes to the software. 
A "contributor" is any person that distributes its contribution under this license. 
"Licensed patents" are a contributor's patent claims that read directly on its contribution. 

2. Grant of Rights 
(A) Copyright Grant- Subject to the terms of this license, including the license conditions 
and limitations in section 3, each contributor grants you a non-exclusive, worldwide, 
royalty-free copyright license to reproduce its contribution, prepare derivative works of 
its contribution, and distribute its contribution or any derivative works that you create. 
(B) Patent Grant- Subject to the terms of this license, including the license conditions 
and limitations in section 3, each contributor grants you a non-exclusive, worldwide, 
royalty-free license under its licensed patents to make, have made, use, sell, offer for 
sale, import, and/or otherwise dispose of its contribution in the software or derivative 
works of the contribution in the software. 

3. Conditions and Limitations 
(A) No Trademark License- This license does not grant you rights to use any contributors' 
name, logo, or trademarks. 
(B) If you bring a patent claim against any contributor over patents that you claim are 
infringed by the software, your patent license from such contributor to the software ends 
automatically. 
(C) If you distribute any portion of the software, you must retain all copyright, patent, 
trademark, and attribution notices that are present in the software. 
(D) If you distribute any portion of the software in source code form, you may do so only 
under this license by including a complete copy of this license with your distribution. 
If you distribute any portion of the software in compiled or object code form, you may only 
do so under a license that complies with this license. 
(E) The software is licensed "as-is." You bear the risk of using it. The contributors give 
no express warranties, guarantees or conditions. You may have additional consumer rights 
under your local laws which this license cannot change. To the extent permitted under your 
local laws, the contributors exclude the implied warranties of merchantability, fitness for 
a particular purpose and non-infringement.

********************************************************************************************/

using System;
using System.Collections;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Permissions;
using System.Windows.Forms;
using Microsoft.VisualStudio.Designer.Interfaces;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell.Interop;

namespace Microsoft.VisualStudio.Project
{

    /// <summary>
    /// The base class for property pages.
    /// </summary>
    [CLSCompliant(false), ComVisible(true)]
    public abstract class SettingsPage :
        LocalizableProperties,
        IPropertyPage,
        IDisposable
    {
        #region fields
        private Panel panel;
        private bool active;
        private bool dirty;
        private IPropertyPageSite site;
        private ProjectNode project;
        private ProjectConfig[] projectConfigs;
        private IVSMDPropertyGrid grid;
        private string name;
        private static readonly object Mutex = new object();
        private bool isDisposed;
        #endregion

        #region properties

        [Browsable(false)]
        [AutomationBrowsable(false)]
        public string Name
        {
            get => this.name;
            set => this.name = value;
        }

        [Browsable(false)]
        [AutomationBrowsable(false)]
        public ProjectNode ProjectMgr => this.project;

        protected IVSMDPropertyGrid Grid => this.grid;

        protected bool IsDirty
        {
            get => this.dirty;
            set
            {
                if(this.dirty != value)
                {
                    this.dirty = value;
                    if(this.site != null)
                        site.OnStatusChange((uint)(this.dirty ? PropPageStatus.Dirty : PropPageStatus.Clean));
                }
            }
        }

        protected Panel ThePanel => this.panel;

        #endregion		

        #region abstract methods
        protected abstract void BindProperties();
        protected abstract int ApplyChanges();
        #endregion

        #region public methods
        public object GetTypedConfigProperty(string name, Type type)
        {
            string value = GetConfigProperty(name);
            
            if(String.IsNullOrEmpty(value))
                return null;

            TypeConverter tc = TypeDescriptor.GetConverter(type);
            return tc.ConvertFromInvariantString(value);
        }

        public object GetTypedProperty(string name, Type type)
        {
            string value = GetProperty(name);

            if(String.IsNullOrEmpty(value))
                return null;

            TypeConverter tc = TypeDescriptor.GetConverter(type);
            return tc.ConvertFromInvariantString(value);
        }

        public string GetProperty(string propertyName)
        {
            if(this.ProjectMgr != null)
            {
                bool found = this.ProjectMgr.BuildProject.GlobalProperties.TryGetValue(propertyName, out string property);

                if(found)
                {
                    return property;
                }
            }

            return String.Empty;
        }

        // relative to active configuration.
        public string GetConfigProperty(string propertyName)
        {
            if(this.ProjectMgr != null)
            {
                string unifiedResult = null;
                bool cacheNeedReset = true;

                for(int i = 0; i < this.projectConfigs.Length; i++)
                {
                    ProjectConfig config = projectConfigs[i];
                    string property = config.GetConfigurationProperty(propertyName, cacheNeedReset);
                    cacheNeedReset = false;

                    if(property != null)
                    {
                        string text = property.Trim();

                        if(i == 0)
                            unifiedResult = text;
                        else if(unifiedResult != text)
                            return ""; // tristate value is blank then
                    }
                }

                return unifiedResult;
            }

            return String.Empty;
        }

        /// <summary>
        /// Sets the value of a configuration dependent property.
        /// If the attribute does not exist it is created.  
        /// If value is null it will be set to an empty string.
        /// </summary>
        /// <param name="name">property name.</param>
        /// <param name="value">value of property</param>
        public void SetConfigProperty(string name, string value)
        {
            CCITracing.TraceCall();
            if(value == null)
            {
                value = String.Empty;
            }

            if(this.ProjectMgr != null)
            {
                for(int i = 0, n = this.projectConfigs.Length; i < n; i++)
                {
                    ProjectConfig config = projectConfigs[i];

                    config.SetConfigurationProperty(name, value);
                }

                this.ProjectMgr.SetProjectFileDirty(true);
            }
        }

        #endregion

        #region IPropertyPage methods.
        public virtual void Activate(IntPtr parent, RECT[] pRect, int bModal)
        {
            if(this.panel == null)
            {
                if (pRect == null)
                {
                    throw new ArgumentNullException(nameof(pRect));
                }

                this.panel = new Panel
                {
                    Size = new Size(pRect[0].right - pRect[0].left, pRect[0].bottom - pRect[0].top),
                    Text = SR.GetString(SR.Settings, CultureInfo.CurrentUICulture),
                    Visible = false
                };

                this.panel.Size = new Size(550, 300);
                this.panel.CreateControl();
                NativeMethods.SetParent(this.panel.Handle, parent);
            }

            if(this.grid == null && this.project != null && this.project.Site != null)
            {
                IVSMDPropertyBrowser pb = this.project.Site.GetService(typeof(IVSMDPropertyBrowser)) as IVSMDPropertyBrowser;
                this.grid = pb.CreatePropertyGrid();
            }

            if(this.grid != null)
            {
                this.active = true;

                Control cGrid = Control.FromHandle(this.grid.Handle);

                cGrid.Parent = Control.FromHandle(parent);//this.panel;
                cGrid.Size = new Size(544, 294);
                cGrid.Location = new Point(3, 3);
                cGrid.Visible = true;
                this.grid.SetOption(_PROPERTYGRIDOPTION.PGOPT_TOOLBAR, false);
                this.grid.GridSort = _PROPERTYGRIDSORT.PGSORT_CATEGORIZED | _PROPERTYGRIDSORT.PGSORT_ALPHABETICAL;

                NativeMethods.SetParent(this.grid.Handle, this.panel.Handle);

                UpdateObjects();
            }
        }

        public virtual int Apply()
        {
            if(IsDirty)
            {
                return this.ApplyChanges();
            }
            return VSConstants.S_OK;
        }

        public virtual void Deactivate()
        {
            if(null != this.panel)
            {
                this.panel.Dispose();
                this.panel = null;
            }
            this.active = false;
        }

        public virtual void GetPageInfo(PROPPAGEINFO[] arrInfo)
        {
            if (arrInfo == null)
            {
                throw new ArgumentNullException(nameof(arrInfo));
            }

            PROPPAGEINFO info = new PROPPAGEINFO
            {
                cb = (uint)Marshal.SizeOf(typeof(PROPPAGEINFO)),
                dwHelpContext = 0,
                pszDocString = null,
                pszHelpFile = null,
                pszTitle = this.name
            };

            info.SIZE.cx = 550;
            info.SIZE.cy = 300;
            
            arrInfo[0] = info;
        }

        public virtual void Help(string helpDir)
        {
        }

        public virtual int IsPageDirty()
        {
            // Note this returns an HRESULT not a Bool.
            return (IsDirty ? (int)VSConstants.S_OK : (int)VSConstants.S_FALSE);
        }

        public virtual void Move(RECT[] arrRect)
        {
            if (arrRect == null)
            {
                throw new ArgumentNullException(nameof(arrRect));
            }
            
            RECT r = arrRect[0];

            this.panel.Location = new Point(r.left, r.top);
            this.panel.Size = new Size(r.right - r.left, r.bottom - r.top);
        }

        public virtual void SetObjects(uint count, object[] punk)
        {
            if (punk == null)
            {
                return;
            }

            if(count > 0)
            {
                if(punk[0] is ProjectConfig)
                {
                    ArrayList configs = new ArrayList();

                    for(int i = 0; i < count; i++)
                    {
                        ProjectConfig config = (ProjectConfig)punk[i];

                        if(this.project == null || (this.project != (punk[0] as ProjectConfig).ProjectMgr))
                        {
                            this.project = config.ProjectMgr;
                        }

                        configs.Add(config);
                    }

                    this.projectConfigs = (ProjectConfig[])configs.ToArray(typeof(ProjectConfig));
                }
                else if(punk[0] is NodeProperties)
                {
                    if (this.project == null || (this.project != (punk[0] as NodeProperties).Node.ProjectMgr))
                    {
                        this.project = (punk[0] as NodeProperties).Node.ProjectMgr;
                    }

                    System.Collections.Generic.Dictionary<string, ProjectConfig> configsMap = new System.Collections.Generic.Dictionary<string, ProjectConfig>();

                    for(int i = 0; i < count; i++)
                    {
                        NodeProperties property = (NodeProperties)punk[i];
                        ErrorHandler.ThrowOnFailure(property.Node.ProjectMgr.GetCfgProvider(out IVsCfgProvider provider));
                        uint[] expected = new uint[1];
                        ErrorHandler.ThrowOnFailure(provider.GetCfgs(0, null, expected, null));
                        if(expected[0] > 0)
                        {
                            ProjectConfig[] configs = new ProjectConfig[expected[0]];
                            uint[] actual = new uint[1];
                            ErrorHandler.ThrowOnFailure(provider.GetCfgs(expected[0], configs, actual, null));

                            foreach(ProjectConfig config in configs)
                            {
                                if(!configsMap.ContainsKey(config.ConfigName))
                                {
                                    configsMap.Add(config.ConfigName, config);
                                }
                            }
                        }
                    }

                    if(configsMap.Count > 0)
                    {
                        if(this.projectConfigs == null)
                        {
                            this.projectConfigs = new ProjectConfig[configsMap.Keys.Count];
                        }
                        configsMap.Values.CopyTo(this.projectConfigs, 0);
                    }
                }
            }
            else
            {
                this.project = null;
            }

            if(this.active && this.project != null)
            {
                UpdateObjects();
            }
        }


        public virtual void SetPageSite(IPropertyPageSite theSite)
        {
            this.site = theSite;
        }

        public virtual void Show(uint cmd)
        {
            this.panel.Visible = true; // TODO: pass SW_SHOW* flags through      
            this.panel.Show();
        }

        public virtual int TranslateAccelerator(MSG[] arrMsg)
        {
            if (arrMsg == null)
            {
                throw new ArgumentNullException(nameof(arrMsg));
            }

            MSG msg = arrMsg[0];

            if((msg.message < NativeMethods.WM_KEYFIRST || msg.message > NativeMethods.WM_KEYLAST) && (msg.message < NativeMethods.WM_MOUSEFIRST || msg.message > NativeMethods.WM_MOUSELAST))
                return 1;

            return (NativeMethods.IsDialogMessageA(this.panel.Handle, ref msg)) ? 0 : 1;
        }

        #endregion

        #region helper methods

        protected ProjectConfig[] GetProjectConfigurations()
        {
            return this.projectConfigs;
        }

        protected void UpdateObjects()
        {
            if(this.projectConfigs != null && this.project != null)
            {
                // Demand unmanaged permissions in order to access unmanaged memory.
                new SecurityPermission(SecurityPermissionFlag.UnmanagedCode).Demand();

                IntPtr p = Marshal.GetIUnknownForObject(this);
                IntPtr ppUnk = Marshal.AllocCoTaskMem(Marshal.SizeOf(typeof(IntPtr)));
                try
                {
                    Marshal.WriteIntPtr(ppUnk, p);

                    this.BindProperties();

                    this.grid.SetSelectedObjects(1, new[] { ppUnk });
                    this.grid.Refresh();
                }
                finally
                {
                    if(ppUnk != IntPtr.Zero)
                    {
                        Marshal.FreeCoTaskMem(ppUnk);
                    }
                    if(p != IntPtr.Zero)
                    {
                        Marshal.Release(p);
                    }
                }
            }
        }
        #endregion

        #region IDisposable Members

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            this.Dispose(true);
            GC.SuppressFinalize(this);
        }


        protected virtual void Dispose(bool disposing)
        {
            if(!this.isDisposed)
            {
                lock(Mutex)
                {
                    if(disposing)
                    {
                        this.panel.Dispose();
                    }

                    this.isDisposed = true;
                }
            }
        }
        #endregion
    }
}
