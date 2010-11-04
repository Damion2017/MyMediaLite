// Copyright (C) 2010 Zeno Gantner, Steffen Rendle, Christoph Freudenthaler
//
// This file is part of MyMediaLite.
//
// MyMediaLite is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// MyMediaLite is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
//  You should have received a copy of the GNU General Public License
//  along with MyMediaLite.  If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using MyMediaLite.data;
using MyMediaLite.data_type;
using MyMediaLite.util;


namespace MyMediaLite.rating_predictor
{
	/// <summary>Simple matrix factorization class</summary>
    /// <remarks>
    /// Factorizing the observed rating values using a feature matrix for users and one for items.
    /// This class can update the factorization online.
    ///
    /// After training, an ArithmeticException is thrown if there are NaN values in the model.
    /// NaN values occur if values become too large or too small to be represented by the type double.
    /// If you encounter such problems, there are three ways to fix them:
    /// (1) (preferred) Use the BiasedMatrixFactorization engine, which is more stable.
    /// (2) Change the range of rating values (1 to 5 works generally well with the default settings).
    /// (3) Change the learn_rate (decrease it if your range is larger than 1 to 5).
    /// </remarks>
    public class MatrixFactorization : Memory, IIterativeModel
    {
		/// <summary>Matrix containing the latent user factors</summary>
        protected Matrix<double> user_factors;
		
		/// <summary>Matrix containing the latent item factors</summary>
        protected Matrix<double> item_factors;
		
		/// <summary>The bias (global average)</summary>
        protected double global_bias;

		/// <summary>Mean of the normal distribution used to initialize the features</summary>
		public double InitMean { get; set; }

        /// <summary>Standard deviation of the normal distribution used to initialize the features</summary>		
		public double InitStdev { get { return init_stdev; } set { init_stdev = value; } }
        private double init_stdev = 0.1;

        /// <summary>Learn rate</summary>
		public double LearnRate { get { return learn_rate; } set { learn_rate = value; } }
        /// <summary>Learn rate</summary>
        protected double learn_rate = 0.01;		
		
        /// <summary>Number of latent features</summary>
		public int NumFactors { get { return num_factors; } set { num_factors = value; }	}
        /// <summary>Number of latent features</summary>
        protected int num_factors = 10;				

        /// <summary>Regularization parameter</summary>
		public double Regularization { get { return regularization; } set { regularization = value; } }
        /// <summary>Regularization parameter</summary>	
        protected double regularization = 0.015;		

		/// <summary>Number of iterations over the training data</summary>
		public int NumIter { get { return num_iter; } set { num_iter = value; } }
        private int num_iter = 30;
		
        /// <inheritdoc />
        public override void Train()
        {
			// init factor matrices
	       	user_factors = new Matrix<double>(ratings.MaxUserID + 1, num_factors);
	       	item_factors = new Matrix<double>(ratings.MaxItemID + 1, num_factors);
	       	MatrixUtils.InitNormal(user_factors, InitMean, InitStdev);
	       	MatrixUtils.InitNormal(item_factors, InitMean, InitStdev);

            // learn model parameters
			ratings.Shuffle(); // avoid effects e.g. if rating data is sorted by user or item
			global_bias = Ratings.All.Average;
            LearnFeatures(ratings.All, true, true);

			// check for NaN in the model
			if (MatrixUtils.ContainsNaN(user_factors))
				throw new ArithmeticException("user_feature contains NaN");
			if (MatrixUtils.ContainsNaN(item_factors))
				throw new ArithmeticException("item_feature contains NaN");
        }

		/// <inheritdoc />
		public virtual void Iterate()
		{
			Iterate(ratings.All, true, true);
		}

        /// <summary>
        /// Updates the latent features on a user
        /// </summary>
        /// <param name="user_id">the user ID</param>
        public void RetrainUser(int user_id)
        {
            MatrixUtils.InitNormal(user_factors, InitMean, InitStdev, user_id);
            LearnFeatures(ratings.ByUser[(int)user_id], true, false);
        }

        /// <summary>Updates the latent features of an item</summary>
        /// <param name="item_id">the item ID</param>
        public void RetrainItem(int item_id)
        {
            MatrixUtils.InitNormal(item_factors, InitMean, InitStdev, item_id);
            LearnFeatures(ratings.ByItem[(int)item_id], false, true);
        }

		/// <summary>
		/// Iterate once over rating data and adjust corresponding features (stochastic gradient descent).
		/// </summary>
		/// <param name="ratings"><see cref="Ratings"/> object containing the ratings to iterate over</param>
		/// <param name="update_user">true if user features to be updated</param>
		/// <param name="update_item">true if item features to be updated</param>
		protected virtual void Iterate(Ratings ratings, bool update_user, bool update_item)
		{
			foreach (RatingEvent rating in ratings)
            {
            	int u = rating.user_id;
                int i = rating.item_id;

                double p = Predict(u, i, false);
				double err = rating.rating - p;

                 // Adjust features, factor by factor
                 for (int f = 0; f < num_factors; f++)
                 {
                 	double u_f = user_factors[u, f];
                    double i_f = item_factors[i, f];

					// compute feature updates
                    double delta_u = (err * i_f - regularization * u_f);
                    double delta_i = (err * u_f - regularization * i_f);

					// if necessary, apply updates
                    if (update_user)
						MatrixUtils.Inc(user_factors, u, f, learn_rate * delta_u);
                    if (update_item)
						MatrixUtils.Inc(item_factors, i, f, learn_rate * delta_i);
                 }
            }
		}

        private void LearnFeatures(Ratings ratings, bool update_user, bool update_item)
        {
            for (int current_iter = 0; current_iter < num_iter; current_iter++)
				Iterate(ratings, update_user, update_item);
        }

        /// <inheritdoc />
        protected double Predict(int user_id, int item_id, bool bound)
        {
            double result = global_bias;

            // U*V
            for (int f = 0; f < num_factors; f++)
                result += user_factors[user_id, f] * item_factors[item_id, f];

            if (bound)
			{
                if (result > MaxRating)
					result = MaxRating;
                if (result < MinRating)
					result = MinRating;
            }
            return result;
        }

		// TODO: use user/item bias for prediction for unknown entities
		
		/// <summary>
		/// Predict the rating of a given user for a given item.
		/// </summary>
		/// <remarks>
		/// If the user or the item are not known to the engine, the global average is returned.
		/// To avoid this behavior for unknown entities, use CanPredict() to check before.
		/// </remarks>
		/// <param name="user_id">the user ID</param>
		/// <param name="item_id">the item ID</param>
		/// <returns>the predicted rating</returns>
        public override double Predict(int user_id, int item_id)
        {
            if (user_id >= user_factors.dim1)
				return global_bias;
            if (item_id >= item_factors.dim1)
				return global_bias;

            return Predict(user_id, item_id, true);
        }

        /// <inheritdoc/>
        public override void AddRating(int user_id, int item_id, double rating)
        {
			base.AddRating(user_id, item_id, rating);
            RetrainUser(user_id);
            RetrainItem(item_id);
        }

        /// <inheritdoc/>
        public override void UpdateRating(int user_id, int item_id, double rating)
        {
			base.UpdateRating(user_id, item_id, rating);
            RetrainUser(user_id);
            RetrainItem(item_id);
        }

        /// <inheritdoc/>
        public override void RemoveRating(int user_id, int item_id)
        {
			base.RemoveRating(user_id, item_id);
            RetrainUser(user_id);
            RetrainItem(item_id);
        }

        /// <inheritdoc/>
        public override void AddUser(int user_id)
        {
			if (user_id > MaxUserID)
			{
            	base.AddUser(user_id);
				user_factors.AddRows(user_id + 1);
            	MatrixUtils.InitNormal(user_factors, InitMean, InitStdev, user_id);
			}
        }

        /// <inheritdoc/>
        public override void AddItem(int item_id)
        {
			if (item_id > MaxItemID)
			{
            	base.AddItem(item_id);
				item_factors.AddRows(item_id + 1);
            	MatrixUtils.InitNormal(item_factors, InitMean, InitStdev, item_id);
			}
        }

        /// <inheritdoc/>
        public override void RemoveUser(int user_id)
        {
            base.RemoveUser(user_id);

			// set user features to zero
            user_factors.SetRowToOneValue(user_id, 0);
        }

        /// <inheritdoc/>
        public override void RemoveItem(int item_id)
        {
            base.RemoveItem(item_id);

			// set item features to zero
            item_factors.SetRowToOneValue(item_id, 0);
        }

        /// <inheritdoc />
		public override void SaveModel(string filePath)
		{
			NumberFormatInfo ni = new NumberFormatInfo();
			ni.NumberDecimalDigits = '.';

			using ( StreamWriter writer = Engine.GetWriter(filePath, this.GetType()) )
			{
            	writer.WriteLine(global_bias.ToString(ni));

            	writer.WriteLine(user_factors.dim1 + " " + user_factors.dim2);
            	for (int i = 0; i < user_factors.dim1; i++)
                	for (int j = 0; j < user_factors.dim2; j++)
                    	writer.WriteLine(i + " " + j + " " + user_factors[i, j].ToString(ni));

            	writer.WriteLine(item_factors.dim1 + " " + item_factors.dim2);
            	for (int i = 0; i < item_factors.dim1; i++)
                	for (int j = 0; j < item_factors.dim2; j++)
                    	writer.WriteLine(i + " " + j + " " + item_factors[i, j].ToString(ni));
			}
		}

		/// <inheritdoc />
		public override void LoadModel(string filePath)
        {
            NumberFormatInfo ni = new NumberFormatInfo();
            ni.NumberDecimalDigits = '.';

            using ( StreamReader reader = Engine.GetReader(filePath, this.GetType()) )
			{
            	double bias = System.Double.Parse(reader.ReadLine(), ni);

            	string[] numbers = reader.ReadLine().Split(' ');
            	int num_users         = System.Int32.Parse(numbers[0]);
            	int num_user_features = System.Int32.Parse(numbers[1]);
            	Matrix<double> user_feature = new Matrix<double>(num_users, num_user_features);

            	while ((numbers = reader.ReadLine().Split(' ')).Length == 3)
            	{
                	int i = System.Int32.Parse(numbers[0]);
                	int j = System.Int32.Parse(numbers[1]);
                	double v = System.Double.Parse(numbers[2], ni);

                	if (i >= num_users)
	                    throw new Exception("i = " + i);
	                if (j >= num_user_features)
	                    throw new Exception("j = " + j);

	                user_feature[i, j] = v;
    	        }

        	    int num_items         = System.Int32.Parse(numbers[0]);
            	int num_item_features = System.Int32.Parse(numbers[1]);
            	if (num_user_features != num_item_features)
                	throw new Exception(string.Format("Number of user and item features must match.", num_user_features, num_item_features));

            	Matrix<double> item_feature = new Matrix<double>(num_items, num_item_features);

            	while (!reader.EndOfStream)
            	{
                	numbers = reader.ReadLine().Split(' ');
                	int i = System.Int32.Parse(numbers[0]);
                	int j = System.Int32.Parse(numbers[1]);
                	double v = System.Double.Parse(numbers[2], ni);

                	if (i >= num_items)
                    	throw new Exception("i = " + i);
	                if (j >= num_item_features)
	                    throw new Exception("j = " + j);

	                item_feature[i, j] = v;
    	        }

				this.MaxUserID = num_users - 1;
				this.MaxItemID = num_items - 1;

            	// assign new model
            	this.global_bias = bias;
				if (this.num_factors != num_user_features)
				{
					Console.Error.WriteLine("Set num_features to {0}", num_user_features);
            		this.num_factors = num_user_features;
				}
            	this.user_factors = user_feature;
            	this.item_factors = item_feature;
			}
        }

		/// <summary>Compute approximated fit (RMSE) on the training data</summary>
		/// <returns>the root mean square error (RMSE) on the training data</returns>
		public double ComputeFit()
		{
			double rmse_sum = 0;
			foreach (RatingEvent rating in ratings)
				rmse_sum += Math.Pow(Predict(rating.user_id, rating.item_id) - rating.rating, 2);

			return Math.Sqrt((double) rmse_sum / ratings.Count);
		}

		/// <inheritdoc />
		public override string ToString()
		{
			NumberFormatInfo ni = new NumberFormatInfo();
			ni.NumberDecimalDigits = '.';						
			
			return string.Format(ni,
			                     "matrix-factorization num_features={0} regularization={1} learn_rate={2} num_iter={3} init_mean={4} init_stdev={5}",
				                 NumFactors, Regularization, LearnRate, NumIter, InitMean, InitStdev);
		}
    }
}
