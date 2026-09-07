//
// This file is released under the terms of the NASA Open Source Agreement (NOSA)
// version 1.3 as detailed in the LICENSE file which accompanies this software.
//

#ifndef HYDROVLMSCREEN_H_
#define HYDROVLMSCREEN_H_

#include "ScreenBase.h"

// OpenVSP-side launcher for the separately built HydroVLM solver.  The solver is
// deliberately not linked into OpenVSP so the VSPAERO and HydroVLM code paths,
// behavior, and license boundaries remain independent.
class HydroVLMScreen : public BasicScreen
{
public:
    HydroVLMScreen( ScreenMgr* mgr );
    virtual ~HydroVLMScreen() {}

    virtual bool Update();
    virtual void GuiDeviceCallBack( GuiDevice* device );
    virtual void CallBack( Fl_Widget* w );
    virtual void CloseCallBack( Fl_Widget* w );

protected:
    GroupLayout m_MainLayout;
    GroupLayout m_BorderLayout;
    StringOutput m_StatusOutput;
    StringOutput m_SurfaceOutput;
    StringOutput m_ModelOutput;
    TriggerButton m_RunButton;
};

#endif
