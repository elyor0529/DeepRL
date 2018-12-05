﻿using System;
using System.Collections.Generic;
using DeepQL.Misc;
using Neuro;
using Neuro.Layers;
using Neuro.Optimizers;
using Neuro.Tensors;

namespace DeepQL.ValueFunc
{
    public class DQN : ValueFunctionModel
    {
        public DQN(Shape inputShape, int numberOfActions, int[] hiddenLayersNeurons, float learningRate, float discountFactor, int replaySize)
            : this(inputShape, numberOfActions, learningRate, discountFactor, replaySize)
        {
            Model = new NeuralNetwork("dqn");
            Model.AddLayer(new Flatten(inputShape));
            for (int i = 0; i < hiddenLayersNeurons.Length; ++i)
                Model.AddLayer(new Dense(Model.LastLayer, hiddenLayersNeurons[i], Activation.ReLU));
            Model.AddLayer(new Dense(Model.LastLayer, numberOfActions, Activation.Linear));
            Model.Optimize(new Adam(learningRate), Loss.Huber1);
        }

        protected DQN(Shape inputShape, int numberOfActions, float learningRate, float discountFactor, int replaySize)
            : base(inputShape, numberOfActions, learningRate, discountFactor)
        {
            ReplayMem = new ReplayMemory(replaySize);

            ErrorChart = new ChartGenerator($"dqn_error", "Q prediction error", "Epoch");
            ErrorChart.AddSeries(0, "Abs error", System.Drawing.Color.LightGray);
            ErrorChart.AddSeries(1, $"Avg({ErrorAvg.N}) abs error", System.Drawing.Color.Firebrick);
        }

        public override Tensor GetOptimalAction(Tensor state)
        {
            var qValues = Model.Predict(state);
            var action = new Tensor(new Shape(1));
            action[0] = qValues.ArgMax();
            return action;
        }

        public override void OnStep(int step, int globalStep, Tensor state, Tensor action, float reward, Tensor nextState, bool done)
        {
            if (globalStep % MemoryInterval == 0)
                ReplayMem.Push(new Transition(state, action, reward, nextState, done));

            if (UsingTargetModel)
            {
                if (TargetModel == null)
                    TargetModel = Model.Clone();

                if (!TargetModelUpdateOnEpisodeEnd)
                {
                    if (TargetModelUpdateInterval >= 1)
                    {
                        if (globalStep % (int)TargetModelUpdateInterval == 0)
                            Model.CopyParametersTo(TargetModel);
                    }
                    else
                        Model.SoftCopyParametersTo(TargetModel, TargetModelUpdateInterval);
                }
            }
        }

        public override void OnTrain()
        {
            if (ReplayMem.StorageSize >= BatchSize)
                Train(ReplayMem.Sample(BatchSize));
        }

        public override void OnEpisodeEnd(int episode)
        {
            if (TargetModelUpdateOnEpisodeEnd)
                Model.CopyParametersTo(TargetModel);

            //if (ChartSaveInterval > 0)
            {
                ErrorAvg.Add(PerEpisodeErrorAvg);

                ErrorChart.AddData(episode, PerEpisodeErrorAvg, 0);
                ErrorChart.AddData(episode, ErrorAvg.Avg, 1);
                ErrorChart.Save();

                PerEpisodeErrorAvg = 0;
                TrainingsDone = 0;
            }
        }

        public override void SaveState(string filename)
        {
            Model.SaveStateXml(filename);
        }

        public override void LoadState(string filename)
        {
            Model.LoadStateXml(filename);
        }

        protected void Train(List<Transition> transitions)
        {
            var stateShape = Model.Layer(0).InputShape;
            Tensor states = new Tensor(new Shape(stateShape.Width, stateShape.Height, stateShape.Depth, transitions.Count));
            Tensor nextStates = new Tensor(states.Shape);

            for (int i = 0; i < transitions.Count; ++i)
            {
                transitions[i].State.CopyBatchTo(0, i, states);
                transitions[i].NextState.CopyBatchTo(0, i, nextStates);
            }

            Tensor rewards = Model.Predict(states); // this is our original prediction
            Tensor futureRewards = (UsingTargetModel ? TargetModel : Model).Predict(nextStates);

            float totalError = 0;

            for (int i = 0; i < transitions.Count; ++i)
            {
                var trans = transitions[i];

                var reward = trans.Reward;
                if (!trans.Done)
                    reward += DiscountFactor * futureRewards.Max(i); // this is the expected prediction for selected action

                float error = reward - rewards[0, (int)trans.Action[0], 0, i];
                totalError += Math.Abs(error);

                rewards[0, (int)trans.Action[0], 0, i] = reward;
            }

            var avgError = totalError / transitions.Count;
            ++TrainingsDone;
            PerEpisodeErrorAvg += (avgError - PerEpisodeErrorAvg) / TrainingsDone;

            Model.Fit(states, rewards, -1, TrainingEpochs, 0, Track.Nothing);
        }

        public override string GetParametersDescription()
        {
            List<int> hiddenInputs = new List<int>();
            for (int i = 2; i < Model.LayersCount; ++i)
                hiddenInputs.Add(Model.Layer(i).InputShape.Length);

            return $"{base.GetParametersDescription()} batch_size={BatchSize} train_epoch={TrainingEpochs} arch={string.Join("|", hiddenInputs)} memory_int={MemoryInterval} target_upd_int={TargetModelUpdateInterval} target_upd_on_ep_end={TargetModelUpdateOnEpisodeEnd}";
        }

        protected bool UsingTargetModel { get { return TargetModelUpdateInterval > 0 || TargetModelUpdateOnEpisodeEnd; } }

        public int BatchSize = 32;
        public int TrainingEpochs = 1;
        // When interval is within (0,1) range, every step soft parameters copy will be performed, otherwise parameters will be copied every interval steps
        public float TargetModelUpdateInterval = 0;
        public bool TargetModelUpdateOnEpisodeEnd = false;
        public int MemoryInterval = 1;
        // Training loss will be clipped to [-DeltaClip, DeltaClip]
        //public float DeltaClip = float.PositiveInfinity;
        public int ChartSaveInterval = 200;
        protected NeuralNetwork Model;
        protected NeuralNetwork TargetModel;
        protected ReplayMemory ReplayMem;
        private readonly ChartGenerator ErrorChart;
        private float PerEpisodeErrorAvg;
        private readonly MovingAverage ErrorAvg = new MovingAverage(100);
        private int TrainingsDone;
    }
}
