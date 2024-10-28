﻿using BigProject.Devices.Arm.Config;
using BigProject.Devices.Arm.CtrlStep;
using BigProject.Devices.Arm.Kinematic.Models;
using BigProject.Devices.Arm.Kinematic;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BigProject.Serials;
using BigProject.Logger;

namespace BigProject.Devices.Arm
{
    public class ArmContrl
    {
        //角度转换成脉冲 3200 为 360°
        public const double DEG_TO_PULSE = 8.888888889;

        public Dof6kinematic dof6Solver;
        public CtrlStepMotor[] motorJ;
        public Joint6D_t lastJoints;
        public Joint6D_t currentJoints;
        public Pose6D_t currentPose6D;
        public ArmSerial serialControl;

        public event Action<double, double, double, double, double, double> UpdateJointAngle;
        public ArmContrl(ArmConfig armConfig,ArmSerial armSerial)
        {
            dof6Solver = new Dof6kinematic(armConfig);
            motorJ = new CtrlStepMotor[6] {
                new CtrlStepMotor(){ AngleLimitMin =-90.1,AngleLimitMax = 0.1 }
                ,new CtrlStepMotor(){ AngleLimitMin = -90.1,AngleLimitMax = 90.1 , Direction =1, OffsetAngle = -90 }
                ,new CtrlStepMotor(){ AngleLimitMin = -0.1,AngleLimitMax= 180.1,OffsetAngle = 180 }
                ,new CtrlStepMotor(){ AngleLimitMin = -0.1,AngleLimitMax = 180.1 , Direction =1}
                ,new CtrlStepMotor(){ AngleLimitMin = -90.1 , AngleLimitMax = 0.1}
                ,new CtrlStepMotor() { AngleLimitMin = -180.1,AngleLimitMax = 180.1}
            };
            serialControl = armSerial;
            currentPose6D = new Pose6D_t();
            currentJoints = new Joint6D_t();
            lastJoints = Joint6D_t.defult;

        }
        //轴移动默认速度
        private double jointSpeed = 50;

        /// <summary>
        /// 根据角度移动轴
        /// </summary>
        /// <param name="_j1">第1轴角度</param>
        /// <param name="_j2">第2轴角度</param>
        /// <param name="_j3">第3轴角度</param>
        /// <param name="_j4">第4轴角度</param>
        /// <param name="_j5">第5轴角度</param>
        /// <param name="_j6">第6轴角度</param>
        /// <returns></returns>
        public bool MoveJ(double _j1, double _j2, double _j3, double _j4, double _j5, double _j6)
        {
            currentJoints = new Joint6D_t(_j1, _j2, _j3, _j4, _j5, _j6);
            dof6Solver.SolveFK(currentJoints, currentPose6D);

            bool valid = true;
            for (int j = 0; j < 6; j++)
            {
                if (currentJoints.a[j] > motorJ[j].AngleLimitMax ||
                    currentJoints.a[j] < motorJ[j].AngleLimitMin)
                {
                    valid = false;
                    Log.Info($"角度{j+1} 限制为{motorJ[j].AngleLimitMin}到{motorJ[j].AngleLimitMax} 当前为{currentJoints.a[j]}");
                }
                    

            }

            if (valid)
            {
                var angleList = (currentJoints - lastJoints).a;
                var maxAngle = AbsMaxOf6(angleList, out int _index);
                if (maxAngle == 0) return true;
                //这里的时间计算忽略加速度影响
                double time = maxAngle * (double)(motorJ[_index].Reduction) / jointSpeed;
                for (int j = 0; j < 6; j++)
                {
                    motorJ[j].Speed = (int)Math.Abs(angleList[j] * 1.0f* motorJ[j].Reduction / time);
                }
                return true;
            }

            return false;
        }

        /// <summary>
        /// 根据最后轴顶点位置移动轴
        /// </summary>
        /// <param name="_x">x坐标</param>
        /// <param name="_y">y坐标</param>
        /// <param name="_z">z坐标</param>
        /// <param name="_a">旋转角 a</param>
        /// <param name="_b">旋转角 b</param>
        /// <param name="_c">旋转角 c</param>
        /// <returns></returns>
        public bool MoveL(double _x, double _y, double _z, double _a, double _b, double _c)
        {
            currentPose6D = new Pose6D_t(_x, _y, _z, _a, _b, _c);
            IKSolves_t ikSolves = new IKSolves_t();
            
            dof6Solver.SolveIK(currentPose6D, lastJoints, out ikSolves);

            bool[] valid = new bool[8];
            int validCnt = 0;

            for (int i = 0; i < 8; i++)
            {
                valid[i] = true;

                for (int j = 0; j < 6; j++)
                {
                    if (ikSolves.config[i].a[j] > motorJ[j].AngleLimitMax ||
                        ikSolves.config[i].a[j] < motorJ[j].AngleLimitMin)
                    {
                        valid[i] = false;
                        continue;
                    }
                }

                if (valid[i]) validCnt++;
            }

            if (validCnt > 0)
            {
                double min = 1000;
                int indexConfig = 0, indexJoint = 0;
                for (int i = 0; i < 8; i++)
                {
                    if (valid[i])
                    {
                        for (int j = 0; j < 6; j++)
                            lastJoints.a[j] = ikSolves.config[i].a[j];
                        Joint6D_t tmp = currentJoints - lastJoints;
                        double maxAngle = AbsMaxOf6(tmp.a, out indexJoint);
                        if (maxAngle < min)
                        {
                            min = maxAngle;
                            indexConfig = i;
                        }
                    }
                }

                return MoveJ(ikSolves.config[indexConfig].a[0], ikSolves.config[indexConfig].a[1],
                             ikSolves.config[indexConfig].a[2], ikSolves.config[indexConfig].a[3],
                             ikSolves.config[indexConfig].a[4], ikSolves.config[indexConfig].a[5]);
            }

            return false;
        }

        /// <summary>
        /// 驱动电机回零操作
        /// </summary>
        public void Homing()
        {
            serialControl.EditZeroParams(addr: 5, direction: 1);
            Thread.Sleep(50);
            serialControl.Zero(5);
            Thread.Sleep(50);

            serialControl.EditZeroParams(addr: 4, direction: 0);
            Thread.Sleep(50);
            serialControl.Zero(4);
            Thread.Sleep(50);

            serialControl.EditZeroParams(addr: 3, direction: 1);
            Thread.Sleep(50);
            serialControl.Zero(3);
            Thread.Sleep(50);

            serialControl.EditZeroParams(addr: 1, direction: 1);
            Thread.Sleep(50);
            serialControl.Zero(1);
            Thread.Sleep(50);

            serialControl.EditZeroParams(addr: 2, direction: 0);
            Thread.Sleep(50);
            serialControl.Zero(2);
            Thread.Sleep(50);

            //第6轴回零
            //Arm6Homing();

            lastJoints = Joint6D_t.defult;
        }

        /// <summary>
        /// 驱动电机移动轴
        /// </summary>
        /// <param name="stepMotor"></param>
        public void MoveJoints()    
        {

            for (int i = 0; i < motorJ.Length; i++)
            {
                var pulse = (int)((currentJoints.a[i] - lastJoints.a[i]) * DEG_TO_PULSE * motorJ[i].Reduction);
                int direction = motorJ[i].Direction;
                if (pulse < 0)
                {
                    direction = direction == 1 ? 0 : 1;
                }
                serialControl.LocationControl(i+1, direction, (int)(motorJ[i].Speed), (int)motorJ[i].Acceleration, Math.Abs(pulse));
                Thread.Sleep(50);
            }
            serialControl.CallMotion();
            lastJoints = currentJoints;
        }

        private double AbsMaxOf6(double[] targetJointsTmp, out int _index)
        {
            var max = Math.Abs(targetJointsTmp[0]);
            _index = 0;
            for (int i = 1; i < targetJointsTmp.Length; i++)
            {
                if (Math.Abs(targetJointsTmp[i]) >  max)
                {
                    max = Math.Abs(targetJointsTmp[i]);
                    _index = i;
                }
            }
            return max;
        }

        /// <summary>
        /// 第6轴单圈回零
        /// </summary>
        public bool Arm6Homing()
        {
            int thisArmId = 6;
            //零位编码器线性值 50962
            var h = 54580;
            //获取编码器当前值
            byte[] send = new byte[3] { 0x06, 0x31, 0x6B };
            serialControl.recCount = 5;
            serialControl.SendMsg(send);
            while (serialControl.DataReceived.Count == 0)
            {
                Thread.Sleep(50);
            }
            var rec = serialControl.DataReceived;
            if (rec.Count==5)
            {
                var t = rec[2]*256 + rec[3];
                //单圈脉冲为3200；
                var angleAbPulse = (int)Math.Abs((h - t) * 1.0 / 65536 * 3200);
                serialControl.LocationControl(thisArmId, 0, 10, Relative_Absolute: RelativeOrAbsolute.Relative,pulse:angleAbPulse, isMultiMachine: 0);
                Thread.Sleep(50);
                //设置当前位为0 位
                SendThisIsZero(thisArmId);
            }
            return true;
        }

        /// <summary>
        /// 立即停止
        /// </summary>
        public void ArmStopNow()
        {
            byte[] send = new byte[5] { 0x00, 0xFE, 0x98, 0x00, 0x6B };
            serialControl.SendMsg(send);
        }

        /// <summary>
        /// 设置当前位置为 0 位
        /// </summary>
        public void SendThisIsZero(int addr)
        {
            //01 0A 6D 6B
            byte[] send = new byte[4] { 0x00, 0x0A ,0x6D, 0x6B };
            send[0] = (byte)addr;
            serialControl.SendMsg(send);
        }

        /// <summary>
        /// 获取电机当前角度位置
        /// </summary>
        /// <param name="addr">地址</param>
        /// <returns></returns>
        public double GetCurrentAngle(int addr, CtrlStepMotor ctrlStep)
        {
            //01 36 6B
            serialControl.recCount = 8;
            byte[] send = new byte[3] { 0x00, 0x36, 0x6B };
            send[0] = (byte)addr;
            serialControl.SendMsg(send);
            while (serialControl.DataReceived.Count == 0)
            {
                Thread.Sleep(50);
            }
            var rec = serialControl.DataReceived;
            int zhengfu = rec[2];
            int anglePulse = ((int)rec[3])*255*255*255 + ((int)rec[4]) * 255 * 255 + ((int)rec[5]) * 255 + rec[6];
            var angle = anglePulse * 1.0 / 65536;
            ctrlStep.CurrentAngle = angle / ctrlStep.Reduction + ctrlStep.OffsetAngle;
            return 0;
        }
    }
}
