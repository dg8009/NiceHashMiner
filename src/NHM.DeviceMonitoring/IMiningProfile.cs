using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NHM.DeviceMonitoring
{
    public interface IMiningProfile
    {
        bool SetMiningProfile(int dmc, int dcc, int mmc, int mcc, string mt);
    }
}
